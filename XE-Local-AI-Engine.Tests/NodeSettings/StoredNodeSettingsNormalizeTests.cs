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
    [Arguments("q5_1")]
    [Arguments("int8")]
    [Arguments("f32")]
    public async Task KvCacheType_Unknown_FallsBackToNull(string type)
    {
        // The normalizer is the layer that must never let a bad value through: LlamaServerLaunchPolicyOptions.Validate()
        // runs in the launch policy's constructor, so a persisted junk value would fail host build rather than degrade.
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            KvCacheType = type
        });
        AssertEx.Null(loaded.KvCacheType);
    }

    [Test]
    [Arguments("f16", "f16")]
    [Arguments("q8_0", "q8_0")]
    [Arguments("Q4_0", "q4_0")]
    [Arguments("  q8_0  ", "q8_0")]
    public async Task KvCacheType_Known_IsKeptCanonical(string type, string expected)
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            KvCacheType = type
        });
        AssertEx.Equal(expected, loaded.KvCacheType);
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

    [Test]
    public async Task Normalize_KeepsAValidExternalAccessProfile()
    {
        await WriteSettingsJsonAsync("{ \"externalAccessProfile\": \"offline\" }");

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, (await LoadAsync()).ExternalAccessProfile);
    }

    [Test]
    public async Task Normalize_KeepsThePendingExternalAccessProfile()
    {
        // "pending" is engine-written at first-run setup, so it must SURVIVE a load: nulling it here would re-arm the
        // boot backfill and decide "recommended" for an operator who has not chosen yet.
        await WriteSettingsJsonAsync("{ \"externalAccessProfile\": \"pending\" }");

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfilePending, (await LoadAsync()).ExternalAccessProfile);
    }

    [Test]
    public async Task Normalize_MapsAnUnknownExternalAccessProfileToPending()
    {
        // Somebody wrote a profile, just not one this engine recognises, so the node is ASKED AGAIN rather than answered
        // for: "pending" keeps the gated services waiting, keeps the switches beside it, and — unlike null — is a
        // non-null profile the boot backfill leaves alone instead of stamping "recommended" over an operator's opt-outs.
        await WriteSettingsJsonAsync("{ \"externalAccessProfile\": \"airgapped\" }");
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfilePending, (await LoadAsync()).ExternalAccessProfile);

        await WriteSettingsJsonAsync("{ \"externalAccessProfile\": \"Offline\" }");
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfilePending, (await LoadAsync()).ExternalAccessProfile);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Normalize_NullsABlankExternalAccessProfile(string profile)
    {
        // Blank is the ONE case that stays null: nobody ever wrote a profile, so this is a legacy install and the boot
        // backfill is allowed to stamp it. Mapping blank to "pending" would instead freeze every upgraded node.
        await WriteSettingsJsonAsync($"{{ \"externalAccessProfile\": \"{profile}\" }}");

        AssertEx.Null((await LoadAsync()).ExternalAccessProfile);
    }

    [Test]
    public async Task Normalize_TrimsButDoesNotCaseFoldTheExternalAccessProfile()
    {
        // Pins the ORDINAL comparison: surrounding whitespace is trimmed away, but a differently-cased literal is not
        // the literal — it loads as "pending". A later case-insensitive change has to edit this test, i.e. it becomes a
        // visible decision.
        await WriteSettingsJsonAsync("{ \"externalAccessProfile\": \"  offline  \" }");
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, (await LoadAsync()).ExternalAccessProfile);

        await WriteSettingsJsonAsync("{ \"externalAccessProfile\": \"Offline\" }");
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfilePending, (await LoadAsync()).ExternalAccessProfile);
    }

    [Test]
    public async Task OldFileMissingTheExternalAccessMembers_LoadsToNull_WithoutThrowing()
    {
        // An upgraded node's file predates all four members. They must deserialize to null — never to a spurious false,
        // which would silently disable every automatic outbound check on every existing install.
        await WriteSettingsJsonAsync("{ \"maxMessageRequestTimeoutSeconds\": 120 }");
        var loaded = await LoadAsync();

        AssertEx.Null(loaded.ExternalAccessProfile);
        AssertEx.Null(loaded.AutoCheckApplicationUpdates);
        AssertEx.Null(loaded.AutoCheckRuntimeUpdates);
        AssertEx.Null(loaded.AutoProvisionFirstRunModel);
    }

    [Test]
    public async Task ExternalAccessSwitches_RoundTripThroughTheStore()
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileOffline,
            AutoCheckApplicationUpdates = false,
            AutoCheckRuntimeUpdates = false,
            AutoProvisionFirstRunModel = false
        });

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, loaded.ExternalAccessProfile);
        AssertEx.Equal(expected: false, loaded.AutoCheckApplicationUpdates);
        AssertEx.Equal(expected: false, loaded.AutoCheckRuntimeUpdates);
        AssertEx.Equal(expected: false, loaded.AutoProvisionFirstRunModel);
    }

    [Test]
    [Arguments(StoredNodeSettings.ExternalAccessProfileRecommended)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileOffline)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileCustom)]
    [Arguments(StoredNodeSettings.ExternalAccessProfilePending)]
    public void IsValidExternalAccessProfile_AcceptsAllFourLiterals(string profile)
    {
        AssertEx.True(StoredNodeSettings.IsValidExternalAccessProfile(profile), $"'{profile}' must be persistable.");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("Recommended")]
    [Arguments("airgapped")]
    public void IsValidExternalAccessProfile_RejectsEverythingElse(string? profile)
    {
        AssertEx.False(StoredNodeSettings.IsValidExternalAccessProfile(profile), $"'{profile}' must not be persistable.");
    }

    [Test]
    [Arguments(StoredNodeSettings.ExternalAccessProfileRecommended, true)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileOffline, true)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileCustom, false)]
    [Arguments(StoredNodeSettings.ExternalAccessProfilePending, false)]
    public void IsExternalAccessPreset_AcceptsOnlyRecommendedAndOffline(string profile, bool expected)
    {
        // The two-predicate split is the whole mechanism keeping "pending" and "custom" un-sendable: the boundary
        // validator gates on THIS predicate, so a client can never claim an engine-written state.
        AssertEx.Equal(expected, StoredNodeSettings.IsExternalAccessPreset(profile));
    }

    [Test]
    public async Task InterruptedSave_LeavesTheStoredOfflineProfileIntact()
    {
        // The save writes a temp sibling and renames it over the target, so a crash between the two leaves a leftover
        // temp file and an INTACT target. Plant exactly that state and prove the stored Offline choice survives: a
        // truncate-then-write save would instead have reverted the node to "undecided" and re-armed the boot backfill.
        using (var store = NewStore())
        {
            await store.SaveAsync(new StoredNodeSettings
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileOffline,
                AutoCheckApplicationUpdates = false,
                AutoCheckRuntimeUpdates = false,
                AutoProvisionFirstRunModel = false
            });
        }

        await File.WriteAllTextAsync(Path.Combine(_root, "node-settings.json.deadbeef.tmp"), "{ \"externalAccessProfile\": ");

        var reloaded = await LoadAsync();

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, reloaded.ExternalAccessProfile);
        AssertEx.Equal(expected: false, reloaded.AutoCheckRuntimeUpdates);
    }

    [Test]
    public async Task Save_LeavesNoTemporaryFileBehind()
    {
        using var store = NewStore();
        await store.SaveAsync(new StoredNodeSettings
        {
            ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileRecommended
        });

        AssertEx.Empty(Directory.GetFiles(_root, "*.tmp"),
            $"A completed save must leave no temp sibling; found [{string.Join(", ", Directory.GetFiles(_root, "*.tmp"))}].");
        AssertEx.True(File.Exists(Path.Combine(_root, "node-settings.json")), "The save must land on node-settings.json.");
    }

    [Test]
    public async Task Save_KeepsTheOwnerOnlyFileModeAcrossTheAtomicMove()
    {
        if (OperatingSystem.IsWindows())
        {
            // A visible skip with a reason, never a silent return; the return keeps the platform analyzer happy.
            Skip.Test("Unix file modes do not exist on Windows; the per-user data-directory ACL governs access there.");
            return;
        }

        // The rename replaces the target with the TEMP file's inode, so the temp must be created 0600 as well. A temp
        // created without UnixCreateMode would silently downgrade the permissions of a file holding the Ollama endpoint
        // and the machine key.
        using var store = NewStore();
        await store.SaveAsync(new StoredNodeSettings
        {
            MachineKey = "not-world-readable"
        });

        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_root, "node-settings.json")));
    }

    [Test]
    public async Task LoadStrictAsync_WhenTheFileIsMissing_ReturnsTheDefaultRecord()
    {
        // A legacy install that has never saved is "no value yet", not an error — the backfill may act on it.
        using var store = NewStore();

        var strict = await store.LoadStrictAsync();

        AssertEx.NotNull(strict);
        AssertEx.Null(strict!.ExternalAccessProfile);
    }

    [Test]
    public async Task LoadStrictAsync_WhenTheFileIsCorrupt_ReturnsNull()
    {
        // The two contracts are pinned against each other on ONE file: the tolerant load still degrades to defaults so
        // no ordinary consumer breaks, while the strict load reports "I cannot read this" so the backfill cannot decide
        // an external-access posture from bytes it never read.
        await WriteSettingsJsonAsync("{ \"externalAccessProfile\": \"offli");
        using var store = NewStore();

        AssertEx.Null(await store.LoadStrictAsync());
        AssertEx.Null((await store.LoadAsync()).ExternalAccessProfile);
    }

    [Test]
    public async Task LoadStrictAsync_WhenTheFileIsValid_ReturnsTheNormalizedRecord()
    {
        await WriteSettingsJsonAsync("{ \"externalAccessProfile\": \"  offline  \", \"llamaMaxLoadedProcesses\": 99 }");
        using var store = NewStore();

        var strict = AssertEx.NotNull(await store.LoadStrictAsync());

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, strict.ExternalAccessProfile);
        AssertEx.Null(strict.LlamaMaxLoadedProcesses);
    }

    [Test]
    public async Task UpdateAsync_WhenTheFileIsUnreadable_ThrowsAndLeavesTheFileByteIdentical()
    {
        // UpdateAsync loads-mutates-saves. Loading TOLERANTLY here would mutate a default record and write it back as
        // valid, so a startup writer would "heal" the corruption into a settings file with a null profile — which the
        // boot backfill then decides "recommended" from, silently re-enabling the outbound checks an operator turned
        // off. Byte-compare, so "left untouched" is proved rather than asserted.
        await WriteSettingsJsonAsync("{ \"externalAccessProfile\": \"offli");
        var path = Path.Combine(_root, "node-settings.json");
        var before = await File.ReadAllBytesAsync(path);

        using var store = NewStore();
        var thrown = await AssertEx.ThrowsAsync<NodeSettingsUnreadableException>(() =>
            store.UpdateAsync(static latest => latest with
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileRecommended
            }));

        AssertEx.Equal(path, thrown.SettingsPath);
        AssertEx.Contains(thrown.Message, "node-settings.json");
        var after = await File.ReadAllBytesAsync(path);
        AssertEx.True(before.SequenceEqual(after), "UpdateAsync must not rewrite a present-but-unreadable settings file.");
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
