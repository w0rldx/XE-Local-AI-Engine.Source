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
        AssertEx.Null(loaded.KeepModelWarmEnabled);
        AssertEx.Null(loaded.KeepModelWarmModelName);
        AssertEx.Null(loaded.KeepModelWarmIntervalSeconds);
        AssertEx.Null(loaded.RecommendedLlamaCppTag);
    }

    [Test]
    public async Task Normalize_WhenAutoEffortFastModelIsBlank_IsNull()
    {
        // Blank is the "Off" signal the select sends, and null is what the dispatcher reads as "this node names no
        // fast model". Whitespace must not survive as a model name nothing can resolve.
        await WriteSettingsJsonAsync("{ \"autoEffortFastModelName\": \"   \" }");
        var blank = await LoadAsync();

        AssertEx.Null(blank.AutoEffortFastModelName);

        await WriteSettingsJsonAsync("{ \"autoEffortFastModelName\": \"  qwen3-1.7b  \" }");
        var trimmed = await LoadAsync();

        AssertEx.Equal("qwen3-1.7b", trimmed.AutoEffortFastModelName);
    }

    [Test]
    public async Task OldFileWithRemovedKeys_LoadsWithoutThrowing_AndKeepsTheSurvivingFields()
    {
        // samplingDefaults (never read at runtime) and allowedVoiceModels (a neural-voice leftover the Web Speech-only
        // client never consumed) were deleted from StoredNodeSettings. A node-settings.json written before the removal
        // still carries both keys, so loading MUST tolerate them: System.Text.Json ignores unknown members unless
        // JsonUnmappedMemberHandling.Disallow is configured, and this pins that the store does not configure it. The
        // fields around them still round-trip.
        await WriteSettingsJsonAsync("""
                                     {
                                       "samplingDefaults": { "seed": "42", "temperature": 0.7 },
                                       "voiceFeatureEnabled": true,
                                       "allowedVoiceModels": ["onnx-community/Kokoro-82M-v1.0-ONNX"],
                                       "defaultVoiceProfile": "af_heart"
                                     }
                                     """);

        var loaded = await LoadAsync();

        AssertEx.Equal(expected: true, loaded.VoiceFeatureEnabled);
        AssertEx.Equal("af_heart", loaded.DefaultVoiceProfile);
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
            KeepModelWarmEnabled = true,
            KeepModelWarmModelName = "  repo/model:Q4_K_M  ",
            KeepModelWarmIntervalSeconds = 300,
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
        AssertEx.Equal("qwen3:8b", loaded.ToolCapableModels[0]);
        AssertEx.Equal("http://127.0.0.1:12345", loaded.OllamaEndpoint);
        AssertEx.Equal("Q5_K_M", loaded.HuggingFaceDefaultQuant);
        AssertEx.Equal(expected: 2_000_000_000L, loaded.HuggingFaceDiskMarginBytes);
        AssertEx.Equal(expected: 8, loaded.LlamaMaxLoadedProcesses);
        AssertEx.Equal(expected: 600, loaded.LlamaIdleTimeToLiveSeconds);
        AssertEx.Equal(expected: true, loaded.KeepModelWarmEnabled);
        AssertEx.Equal("repo/model:Q4_K_M", loaded.KeepModelWarmModelName);
        AssertEx.Equal(expected: 300, loaded.KeepModelWarmIntervalSeconds);
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
    [Arguments(4)]
    [Arguments(3601)]
    public async Task KeepModelWarmIntervalSeconds_OutOfRange_FallsBackToNull(int value)
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            DefaultModelName = "keep-unrelated",
            KeepModelWarmIntervalSeconds = value
        });

        AssertEx.Null(loaded.KeepModelWarmIntervalSeconds);
        AssertEx.Equal("keep-unrelated", loaded.DefaultModelName);
    }

    [Test]
    public async Task KeepModelWarmModelName_Blank_FallsBackToNull()
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            KeepModelWarmModelName = "   "
        });

        AssertEx.Null(loaded.KeepModelWarmModelName);
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
    public async Task SpeculativeAndCacheReuse_WithinRange_RoundTrip()
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            ChatCacheReuse = 512,
            SpeculativeMode = "draft-simple",
            SpeculativeDraftModelName = "  draft-model  ",
            SpeculativeDraftMaxTokens = 5,
            SpeculativeDraftGpuLayers = 12
        });

        AssertEx.Equal(expected: 512, loaded.ChatCacheReuse);
        AssertEx.Equal("draft-simple", loaded.SpeculativeMode);
        AssertEx.Equal("draft-model", loaded.SpeculativeDraftModelName);
        AssertEx.Equal(expected: 5, loaded.SpeculativeDraftMaxTokens);
        AssertEx.Equal(expected: 12, loaded.SpeculativeDraftGpuLayers);
    }

    [Test]
    public async Task ChatCacheReuse_Zero_IsKept_AsDisableSentinel()
    {
        // 0 is the "disabled" value, inside [0, 8192], so it must survive normalization (not fall back to null/default).
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            ChatCacheReuse = 0
        });
        AssertEx.Equal(expected: 0, loaded.ChatCacheReuse);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(8193)]
    public async Task ChatCacheReuse_OutOfRange_FallsBackToNull(int value)
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            ChatCacheReuse = value
        });
        AssertEx.Null(loaded.ChatCacheReuse);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(17)]
    public async Task SpeculativeDraftMaxTokens_OutOfRange_FallsBackToNull(int value)
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            SpeculativeDraftMaxTokens = value
        });
        AssertEx.Null(loaded.SpeculativeDraftMaxTokens);
    }

    [Test]
    [Arguments("not-a-real-mode")]
    [Arguments("draft-bogus")]
    public async Task SpeculativeMode_Unknown_FallsBackToNull(string mode)
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            SpeculativeMode = mode
        });
        AssertEx.Null(loaded.SpeculativeMode);
    }

    [Test]
    [Arguments("ngram-mod")]
    [Arguments("draft-eagle3")]
    [Arguments("none")]
    public async Task SpeculativeMode_Known_IsKept(string mode)
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            SpeculativeMode = mode
        });
        AssertEx.Equal(mode, loaded.SpeculativeMode);
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

    [Test]
    public async Task UsageRates_ValidEntries_RoundTrip_TrimKeys_DropInvalid()
    {
        // The persistence authority for usage-rate hygiene: trim keys, drop blank keys and negative rates, and match
        // case-insensitively after a JSON round trip (which loses the comparer). Only the one valid entry survives.
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            UsageRates = new NodeUsageRateSettings
            {
                Models = new Dictionary<string, ModelRate>
                {
                    ["  gpt-5  "] = new()
                    {
                        InputPer1M = 1.25,
                        OutputPer1M = 10
                    },
                    ["bad-negative"] = new()
                    {
                        InputPer1M = -1,
                        OutputPer1M = 5
                    },
                    ["   "] = new()
                    {
                        InputPer1M = 1,
                        OutputPer1M = 1
                    }
                }
            }
        });

        AssertEx.NotNull(loaded.UsageRates);
        AssertEx.NotNull(loaded.UsageRates!.Models);
        AssertEx.Equal(expected: 1, loaded.UsageRates.Models!.Count);
        // Key was trimmed to "gpt-5" and is now matched case-insensitively.
        AssertEx.Equal(expected: 1.25d, loaded.UsageRates.Models["GPT-5"].InputPer1M);
        AssertEx.Equal(expected: 10d, loaded.UsageRates.Models["GPT-5"].OutputPer1M);
    }

    [Test]
    public async Task UsageRates_AllInvalid_FallsBackToNull()
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            UsageRates = new NodeUsageRateSettings
            {
                Models = new Dictionary<string, ModelRate>
                {
                    ["bad"] = new()
                    {
                        InputPer1M = -1,
                        OutputPer1M = -1
                    }
                }
            }
        });

        AssertEx.Null(loaded.UsageRates);
    }

    [Test]
    public async Task DetachedGraceSeconds_ClampsNegativesToZero_AndReSeedsAnAbsurdValue()
    {
        // Unlike every other numeric field, a NEGATIVE grace clamps to 0 instead of re-seeding: 0 is a meaningful value
        // here (never cancel), so "the operator asked for no reaping, badly" beats "silently reap at 300 s anyway".
        AssertEx.Equal(expected: 0, (await SaveAndReloadAsync(new StoredNodeSettings
        {
            DetachedGraceSeconds = -1
        })).DetachedGraceSeconds);
        AssertEx.Equal(expected: 0, (await SaveAndReloadAsync(new StoredNodeSettings
        {
            DetachedGraceSeconds = 0
        })).DetachedGraceSeconds);
        AssertEx.Equal(expected: 300, (await SaveAndReloadAsync(new StoredNodeSettings
        {
            DetachedGraceSeconds = 300
        })).DetachedGraceSeconds);

        // Above the guard it falls back to null like the rest, so the accessor re-seeds it.
        AssertEx.Null((await SaveAndReloadAsync(new StoredNodeSettings
        {
            DetachedGraceSeconds = StoredNodeSettings.MaxDetachedGraceSeconds + 1
        })).DetachedGraceSeconds);
    }

    [Test]
    public async Task DetachedGraceSeconds_AbsentFromAnOldFile_StaysNull()
    {
        // A node-settings.json written before the field existed must deserialize to null — and then re-seed to 300 —
        // not to a spurious 0 that would silently disable reaping on every upgraded node.
        await WriteSettingsJsonAsync("{ \"maxMessageRequestTimeoutSeconds\": 120 }");
        var loaded = await LoadAsync();

        AssertEx.Null(loaded.DetachedGraceSeconds);
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
