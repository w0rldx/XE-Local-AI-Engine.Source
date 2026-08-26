namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkJudgePolicyContractsTests
{
    [Test]
    public void Validator_AcceptsEveryDefaultRubric()
    {
        BenchmarkJudgePolicyValidator.Validate(Policy(BenchmarkJudgeRubricDefaults.Default()));
        BenchmarkJudgePolicyValidator.Validate(Policy(BenchmarkJudgeRubricDefaults.Programming()));
        BenchmarkJudgePolicyValidator.Validate(Policy(BenchmarkJudgeRubricDefaults.Reasoning()));

        AssertEx.Equal(100, BenchmarkJudgeRubricDefaults.Default().Criteria.Sum(static criterion => criterion.Weight));
        AssertEx.True(BenchmarkJudgeRubricDefaults.Programming().Criteria.Select(static criterion => criterion.Id)
                                                  .SequenceEqual(BenchmarkJudgeRubricDefaults.Default().Criteria.Select(static criterion => criterion.Id)));
    }

    [Test]
    public void VerifiablePreset_ActivatesAndNeedsNoJudgeModel()
    {
        var rubric = BenchmarkJudgeRubricDefaults.Verifiable();

        BenchmarkJudgePolicyValidator.Validate(Policy(rubric));

        AssertEx.Equal(100, rubric.Criteria.Sum(static criterion => criterion.Weight));
        AssertEx.True(rubric.Criteria.All(static criterion => BenchmarkJudgeCriterionKinds.IsVerifiable(criterion.Kind)),
            "Every criterion must be verifiable, or the preset still spawns a judge.");
        AssertEx.True(rubric.Criteria.All(static criterion => BenchmarkJudgeVerifierConfig.Parse(criterion.Kind, criterion.Config) is not null));
    }

    [Test]
    public void Validator_RejectsCriterionCountOutOfRange()
    {
        var empty = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([]))));
        var nine = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([.. Enumerable.Range(0, 9).Select(index => Criterion($"c{index}"))]))));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionCountOutOfRange, empty.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionCountOutOfRange, nine.Code);
    }

    [Test]
    public void Validator_AcceptsCriterionCountBoundaries()
    {
        BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion("c0")])));
        BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([.. Enumerable.Range(0, 8).Select(index => Criterion($"c{index}"))])));
    }

    [Test]
    public void Validator_RejectsInvalidAndDuplicateCriterionIds()
    {
        var upper = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion("Correctness")]))));
        var space = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion("a b")]))));
        var empty = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion(string.Empty)]))));
        var tooLong = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion(new string('a', 33))]))));
        var duplicate = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion("dup"), Criterion("dup")]))));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionIdInvalid, upper.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionIdInvalid, space.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionIdInvalid, empty.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionIdInvalid, tooLong.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionIdDuplicate, duplicate.Code);
        BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion(new string('a', 32))])));
    }

    [Test]
    public void Validator_RejectsWeightOutsideOneToHundred()
    {
        var zero = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion("c0", weight: 0)]))));
        var over = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion("c0", weight: 101)]))));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionWeightOutOfRange, zero.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionWeightOutOfRange, over.Code);
        BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion("c0", weight: 1)])));
        BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([Criterion("c0", weight: 100)])));
    }

    [Test]
    public void Validator_RejectsTitleAndDescriptionOutsideBounds()
    {
        var title = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([
                Criterion("c0") with
                {
                    Title = new string('t', 65)
                }
            ]))));
        var description = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([
                Criterion("c0") with
                {
                    Description = "  "
                }
            ]))));
        var longDescription = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([
                Criterion("c0") with
                {
                    Description = new string('d', 1025)
                }
            ]))));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionTitleInvalid, title.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionDescriptionInvalid, description.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionDescriptionInvalid, longDescription.Code);
        BenchmarkJudgePolicyValidator.Validate(Policy(Rubric([
            Criterion("c0") with
            {
                Title = new string('t', 64),
                Description = new string('d', 1024)
            }
        ])));
    }

    [Test]
    public void Validator_RejectsUnsupportedVersionsAndContextAndReferenceAnswer()
    {
        var prompt = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(Policy(BenchmarkJudgeRubricDefaults.Default()) with
            {
                PromptVersion = 1
            }));
        var schema = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(Policy(BenchmarkJudgeRubricDefaults.Default()) with
            {
                OutputSchemaVersion = 3
            }));
        var rubricVersion = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(Policy(BenchmarkJudgeRubricDefaults.Default() with
            {
                Version = 2
            })));
        var context = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(Policy(BenchmarkJudgeRubricDefaults.Default()) with
            {
                RequestedContextTokens = 0
            }));
        var reference = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(Policy(BenchmarkJudgeRubricDefaults.Default()) with
            {
                ReferenceAnswer = new string('r', 32769)
            }));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.PromptVersionUnsupported, prompt.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.OutputSchemaVersionUnsupported, schema.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.RubricVersionUnsupported, rubricVersion.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.ContextTokensInvalid, context.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.ReferenceAnswerTooLong, reference.Code);
        BenchmarkJudgePolicyValidator.Validate(Policy(BenchmarkJudgeRubricDefaults.Default()) with
        {
            ReferenceAnswer = new string('r', 32768)
        });
        BenchmarkJudgePolicyValidator.Validate(Policy(BenchmarkJudgeRubricDefaults.Default()) with
        {
            ReferenceAnswer = null
        });
    }

    [Test]
    public void Canonicalizer_IsInsensitiveToCriterionAndMemberOrder()
    {
        var criteria = BenchmarkJudgeRubricDefaults.Default().Criteria;
        var forward = Policy(BenchmarkJudgeRubricDefaults.Default());
        var reversed = Policy(BenchmarkJudgeRubricDefaults.Default() with
            {
                Criteria = [.. criteria.Reverse()]
            })
            with
            {
                Model = Model() with
                {
                    MemberHashes = ["bbb", "aaa"]
                }
            };

        AssertEx.Equal(BenchmarkJudgePolicyCanonicalizer.ToCanonicalJson(forward), BenchmarkJudgePolicyCanonicalizer.ToCanonicalJson(reversed));
        AssertEx.Equal(BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(forward), BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(reversed));
    }

    [Test]
    public void Canonicalizer_EmitsCompactCamelCaseJsonWithExplicitNulls()
    {
        // Space-free texts, so any space left in the output is formatting whitespace rather than rubric wording.
        var canonical = BenchmarkJudgePolicyCanonicalizer.ToCanonicalJson(Policy(Rubric([new BenchmarkJudgeRubricCriterionV1("c0", "Title", "Description", 10)])));

        AssertEx.False(canonical.Contains(' ', StringComparison.Ordinal));
        AssertEx.False(canonical.Contains('\n', StringComparison.Ordinal));
        AssertEx.Contains(canonical, "\"referenceAnswer\":null");
        AssertEx.Contains(canonical, "\"requestedContextTokens\":4096");
        // Against the constant, not a literal: the prompt version moves whenever the judge's system prompt is reworded,
        // and this assertion is about the canonical SHAPE, not about which version is current.
        AssertEx.Contains(canonical, $"\"promptVersion\":{BenchmarkJudgePolicyVersions.PromptVersion}");
        using var document = JsonDocument.Parse(canonical);
        AssertEx.True(document.RootElement.EnumerateObject().Select(static property => property.Name)
                              .SequenceEqual(PolicyMemberNames, StringComparer.Ordinal));
    }

    [Test]
    public void ComputePolicyHash_IsLowercaseSha256HexAndChangesWithEveryField()
    {
        var baseline = Policy(BenchmarkJudgeRubricDefaults.Default());
        var hash = BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(baseline);

        AssertEx.Equal(64, hash.Length);
        AssertEx.True(hash.All(static character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
        AssertEx.Equal(hash, BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(Policy(BenchmarkJudgeRubricDefaults.Default())));
        foreach (var mutated in Mutations(baseline))
        {
            AssertEx.NotEqual(hash, BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(mutated));
        }
    }

    [Test]
    public void Canonicalizer_PinsPropertyOrderAtEveryLevelAndIsIndependentOfConstructionOrder()
    {
        var rubric = Rubric([Criterion("beta"), Criterion("alpha")]);
        var forward = Policy(rubric);
        // Same policy, built the other way round at every level that has one.
        var rebuilt = Policy(Rubric([Criterion("alpha"), Criterion("beta")])) with
        {
            Model = Model() with
            {
                MemberHashes = ["bbb", "aaa"]
            }
        };

        var canonical = BenchmarkJudgePolicyCanonicalizer.ToCanonicalJson(forward);

        AssertEx.True(Encoding.UTF8.GetBytes(canonical)
                              .SequenceEqual(Encoding.UTF8.GetBytes(BenchmarkJudgePolicyCanonicalizer.ToCanonicalJson(rebuilt))));
        using var document = JsonDocument.Parse(canonical);
        var root = document.RootElement;
        AssertEx.True(Names(root).SequenceEqual(PolicyMemberNames, StringComparer.Ordinal));
        AssertEx.True(Names(root.GetProperty("model")).SequenceEqual(["modelName", "modelContentFingerprint", "memberHashes"], StringComparer.Ordinal));
        AssertEx.True(Names(root.GetProperty("sampling")).SequenceEqual(
            ["temperature", "topP", "topK", "minP", "maxOutputTokens", "repeatPenalty", "repeatLastN", "presencePenalty", "frequencyPenalty", "stop", "seedPolicy", "seedValue"],
            StringComparer.Ordinal));
        AssertEx.True(Names(root.GetProperty("rubric")).SequenceEqual(["version", "criteria"], StringComparer.Ordinal));
        AssertEx.True(Names(root.GetProperty("rubric").GetProperty("criteria")[0]).SequenceEqual(CriterionMemberNames, StringComparer.Ordinal));
        AssertEx.Equal("alpha", root.GetProperty("rubric").GetProperty("criteria")[0].GetProperty("id").GetString());
    }

    /// <summary>The canonical policy member order, pinned once so the two shape tests cannot drift apart.</summary>
    internal static readonly string[] PolicyMemberNames =
    [
        "model", "requestedContextTokens", "promptVersion", "outputSchemaVersion", "sampling", "rubric", "referenceAnswer",
        "mode", "pairwisePromptVersion", "pairwiseOutputSchemaVersion"
    ];

    internal static readonly string[] CriterionMemberNames = ["id", "title", "description", "weight", "kind", "config"];

    private static IEnumerable<string> Names(JsonElement element) =>
        element.EnumerateObject().Select(static property => property.Name);

    private static IEnumerable<BenchmarkJudgePolicyV1> Mutations(BenchmarkJudgePolicyV1 baseline)
    {
        yield return baseline with
        {
            ReferenceAnswer = "reference"
        };
        yield return baseline with
        {
            RequestedContextTokens = 8192
        };
        yield return baseline with
        {
            Model = baseline.Model with
            {
                ModelName = "other"
            }
        };
        yield return baseline with
        {
            Model = baseline.Model with
            {
                ModelContentFingerprint = $"v1:{new string('b', 64)}"
            }
        };
        yield return baseline with
        {
            Model = baseline.Model with
            {
                MemberHashes = ["aaa"]
            }
        };
        yield return baseline with
        {
            Sampling = baseline.Sampling with
            {
                Temperature = 0.5f
            }
        };
        yield return baseline with
        {
            Rubric = baseline.Rubric with
            {
                Criteria =
                [
                    .. baseline.Rubric.Criteria.Select(static (criterion, index) => index == 0
                        ? criterion with
                        {
                            Weight = 41
                        }
                        : criterion)
                ]
            }
        };
        yield return baseline with
        {
            Rubric = baseline.Rubric with
            {
                Criteria =
                [
                    .. baseline.Rubric.Criteria.Select(static (criterion, index) => index == 0
                        ? criterion with
                        {
                            Description = "changed"
                        }
                        : criterion)
                ]
            }
        };
        yield return baseline with
        {
            Rubric = baseline.Rubric with
            {
                Criteria =
                [
                    .. baseline.Rubric.Criteria.Select(static (criterion, index) => index == 0
                        ? criterion with
                        {
                            Title = "changed"
                        }
                        : criterion)
                ]
            }
        };
    }

    internal static BenchmarkJudgePolicyModelV1 Model() =>
        new("judge-model", $"v1:{new string('a', 64)}", ["aaa", "bbb"]);

    [Test]
    public void Validate_OnRead_AcceptsAnOlderStoredVersionButStillChecksStructure()
    {
        // The read path must never be able to reject a row this build could have written. When it could, bumping
        // PromptVersion made GET benchmarks/projects/{id} throw, the whole project header vanished from the UI, and it
        // took the re-save control that heals the revision with it.
        var stored = Policy(BenchmarkJudgeRubricDefaults.Default()) with
        {
            PromptVersion = BenchmarkJudgePolicyVersions.PromptVersion - 1
        };

        BenchmarkJudgePolicyValidator.Validate(stored, strictVersions: false);
        var written = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(stored));
        var structural = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgePolicyValidator.Validate(stored with
            {
                RequestedContextTokens = 0
            }, strictVersions: false));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.PromptVersionUnsupported, written.Code, "Writing one is still refused.");
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.ContextTokensInvalid, structural.Code,
            "Structure is still enforced on read; only the version gate is relaxed.");
        AssertEx.False(BenchmarkJudgePolicyValidator.VersionsAreCurrent(stored), "An older prompt version is not current.");
        AssertEx.True(BenchmarkJudgePolicyValidator.VersionsAreCurrent(Policy(BenchmarkJudgeRubricDefaults.Default())));
    }

    [Test]
    public void DeserializePolicy_ForAStoredOlderPromptVersion_RoundTripsItVerbatim()
    {
        // Read back verbatim, not silently upgraded: the stored bytes must still re-hash to the stored PolicyHash, and
        // the version is what tells the executor to refuse and the UI to offer the re-save.
        var stored = Policy(BenchmarkJudgeRubricDefaults.Default()) with
        {
            PromptVersion = BenchmarkJudgePolicyVersions.PromptVersion - 1
        };

        var read = BenchmarkJudgeSerialization.DeserializePolicy(BenchmarkJudgeSerialization.SerializePolicy(stored));

        AssertEx.Equal(BenchmarkJudgePolicyVersions.PromptVersion - 1, read.PromptVersion);
    }

    [Test]
    public void Mode_DefaultsToPointwiseAndPairwiseIsRefusedUntilS3()
    {
        var baseline = Policy(BenchmarkJudgeRubricDefaults.Default());

        AssertEx.Equal(BenchmarkJudgePolicyModes.Pointwise, baseline.Mode, "A policy built without a mode judges pointwise.");
        BenchmarkJudgePolicyValidator.Validate(baseline);

        // Pairwise is executable now, so it validates like any other mode; only an unknown mode is refused.
        BenchmarkJudgePolicyValidator.Validate(baseline with
        {
            Mode = BenchmarkJudgePolicyModes.Pairwise
        });
        var nonsense = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(baseline with
        {
            Mode = "coinflip"
        }));
        var pairwiseVersion = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(baseline with
        {
            PairwisePromptVersion = BenchmarkJudgePolicyVersions.PairwisePromptVersion + 1
        }));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.ModeUnsupported, nonsense.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.PairwiseVersionUnsupported, pairwiseVersion.Code);
    }

    [Test]
    public void Validate_OnRead_ToleratesAModeAndAPairwiseVersionItWouldRefuseToWrite()
    {
        // Same rule the prompt version lives under: a row this build could one day have written must stay READABLE,
        // or the project header disappears from the UI along with the control that heals it.
        var stored = Policy(BenchmarkJudgeRubricDefaults.Default()) with
        {
            Mode = BenchmarkJudgePolicyModes.Pairwise,
            PairwiseOutputSchemaVersion = BenchmarkJudgePolicyVersions.PairwiseOutputSchemaVersion + 1
        };

        BenchmarkJudgePolicyValidator.Validate(stored, strictVersions: false);
    }

    [Test]
    public void LegacyPolicyJson_WithoutModeOrKind_DeserializesToTheDefaults()
    {
        // The exact bytes a pre-P2 build wrote: no mode, no pairwise versions, no criterion kind or config.
        const string LegacyJson =
            """
            {"model":{"modelName":"judge-model","modelContentFingerprint":"v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","memberHashes":["aaa","bbb"]},"requestedContextTokens":4096,"promptVersion":3,"outputSchemaVersion":2,"sampling":{"temperature":0,"topP":null,"topK":null,"minP":null,"maxOutputTokens":null,"repeatPenalty":null,"repeatLastN":null,"presencePenalty":null,"frequencyPenalty":null,"stop":[],"seedPolicy":"fixed","seedValue":"0"},"rubric":{"version":1,"criteria":[{"id":"correctness","title":"T","description":"D","weight":100}]},"referenceAnswer":null}
            """;

        var read = BenchmarkJudgeSerialization.DeserializePolicy(Encoding.UTF8.GetBytes(LegacyJson));

        AssertEx.Equal(BenchmarkJudgePolicyModes.Pointwise, read.Mode);
        AssertEx.Equal(BenchmarkJudgePolicyVersions.PairwisePromptVersion, read.PairwisePromptVersion);
        AssertEx.Equal(BenchmarkJudgePolicyVersions.PairwiseOutputSchemaVersion, read.PairwiseOutputSchemaVersion);
        AssertEx.Equal(BenchmarkJudgeCriterionKinds.Llm, read.Rubric.Criteria[0].Kind);
        AssertEx.Null(read.Rubric.Criteria[0].Config);
    }

    [Test]
    public void ComputePolicyHash_SeparatesModeAndEveryCriterionKindAndConfig()
    {
        // The precondition the `verified:v1` sentinel rests on (plan §7.3, §20 nit): one policy revision provably
        // means ONE rubric composition, because the mode, every criterion kind and every criterion config are inside
        // the hash. If any of them ever left it, a constant execution key would start merging attempts that were
        // graded differently and the sentinel would have to be revisited with it.
        var baseline = Policy(BenchmarkJudgeRubricDefaults.Default());
        var hash = BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(baseline);
        var verifiable = baseline with
        {
            Rubric = Rubric([Criterion("correctness") with
            {
                Kind = BenchmarkJudgeCriterionKinds.Exact,
                Config = """{"expected":"4"}"""
            }])
        };

        AssertEx.NotEqual(hash, BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(baseline with
        {
            Mode = BenchmarkJudgePolicyModes.Pairwise
        }));
        AssertEx.NotEqual(hash, BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(baseline with
        {
            PairwisePromptVersion = 2
        }));
        AssertEx.NotEqual(hash, BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(baseline with
        {
            PairwiseOutputSchemaVersion = 2
        }));
        AssertEx.NotEqual(BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(verifiable),
            BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(verifiable with
            {
                Rubric = Rubric([Criterion("correctness") with
                {
                    Kind = BenchmarkJudgeCriterionKinds.Exact,
                    Config = """{"expected":"5"}"""
                }])
            }),
            "Editing a verifier's expected answer must mint a new revision.");
        AssertEx.NotEqual(BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(verifiable),
            BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(baseline with
            {
                Rubric = Rubric([Criterion("correctness")])
            }));
    }

    [Test]
    public void Canonicalizer_NormalizesTheKindAndTheConfigItHashes()
    {
        var spaced = Policy(Rubric([Criterion("c0") with
        {
            Kind = BenchmarkJudgeCriterionKinds.Exact,
            Config = """{ "normalize" : { "trim" : true } , "expected" : "4" }"""
        }]));
        var compact = Policy(Rubric([Criterion("c0") with
        {
            Kind = BenchmarkJudgeCriterionKinds.Exact,
            Config = """{"expected":"4","normalize":{"trim":true}}"""
        }]));

        AssertEx.Equal(BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(spaced), BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(compact));
    }

    [Test]
    public void ValidateRubric_ParsesEveryVerifiableConfigAtActivationOnly()
    {
        var broken = Policy(Rubric([Criterion("c0") with
        {
            Kind = BenchmarkJudgeCriterionKinds.Regex,
            Config = """{"pattern":"(?=a)b"}"""
        }]));

        var write = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgePolicyValidator.Validate(broken));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, write.Code);
        // Read stays tolerant: a revision stored under an older build must still open.
        BenchmarkJudgePolicyValidator.Validate(broken, strictVersions: false);
    }

    internal static BenchmarkJudgeRubricCriterionV1 Criterion(string id, int weight = 10) =>
        new(id, $"Title {id}", $"Description {id}. 0 = poor; 5 = partial; 10 = excellent.", weight);

    internal static BenchmarkJudgeRubricV1 Rubric(IReadOnlyList<BenchmarkJudgeRubricCriterionV1> criteria) =>
        new(BenchmarkJudgePolicyVersions.RubricVersion, criteria);

    internal static BenchmarkJudgePolicyV1 Policy(BenchmarkJudgeRubricV1 rubric) =>
        new(Model(),
            4096,
            BenchmarkJudgePolicyVersions.PromptVersion,
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
            rubric,
            ReferenceAnswer: null);
}
