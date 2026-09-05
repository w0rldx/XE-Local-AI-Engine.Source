namespace XE_Local_AI_Engine.Tests.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Client.Services.Validation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

public sealed class RuntimePackageValidatorTests
{
    private readonly RuntimePackageValidator _validator;

    public RuntimePackageValidatorTests()
    {
        var securityOptions = Options.Create(new SecurityOptions
        {
            MaxSystemPromptSizeKb = 1,
            MaxMessageSizeKb = 1,
            AllowedModelNamePattern = "^[a-zA-Z0-9._:-]+$"
        });

        _validator = new RuntimePackageValidator(new ModelNameValidator(securityOptions), securityOptions);
    }

    [Test]
    public void Validate_WhenPackageIsValid_ReturnsValid()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().Build());

        AssertEx.True(result.IsValid);
        AssertEx.Empty(result.Errors);
    }

    [Test]
    public void Validate_WhenSystemPromptIsNull_ReturnsError()
    {
        var package = RuntimePackageBuilder.Valid().Build() with
        {
            ResolvedSystemPrompt = null!
        };

        var result = _validator.Validate(package);

        AssertErrorContains(result, "system prompt");
    }

    [Test]
    public void Validate_WhenSystemPromptExceedsLimit_ReturnsError()
    {
        var package = RuntimePackageBuilder.Valid()
                                           .WithSystemPrompt(new string(c: 'a', count: 1025))
                                           .Build();

        var result = _validator.Validate(package);

        AssertErrorContains(result, "system prompt");
    }

    [Test]
    public void Validate_WhenSystemPromptContainsNullByte_ReturnsError()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithSystemPrompt("abc\0def").Build());

        AssertErrorContains(result, "system prompt");
    }

    [Test]
    public void Validate_WhenSystemPromptContainsScriptTag_ReturnsError()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithSystemPrompt("<script>alert(1)</script>").Build());

        AssertErrorContains(result, "system prompt");
    }

    [Test]
    public void Validate_WhenSystemPromptContainsDoctype_ReturnsError()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithSystemPrompt("<!DOCTYPE html>").Build());

        AssertErrorContains(result, "system prompt");
    }

    [Test]
    public void Validate_WhenSystemPromptContainsXmlDeclaration_ReturnsError()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithSystemPrompt("<?xml version=\"1.0\"?>").Build());

        AssertErrorContains(result, "system prompt");
    }

    [Test]
    public void Validate_WhenModelNameIsInvalid_ReturnsError()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithModel("foo/bar").Build());

        AssertErrorContains(result, "model identifier");
    }

    [Test]
    public void Validate_WhenModelNameIsNull_ReturnsValid()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithModel(null).Build());

        AssertEx.True(result.IsValid);
    }

    [Test]
    public void Validate_WhenMessageExceedsLimit_ReturnsError()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithUserMessage(new string(c: 'b', count: 1025)).Build());

        AssertErrorContains(result, "conversation message");
    }

    [Test]
    public void Validate_WhenMessageExceedsLimit_AndTheSizeCapIsNotEnforced_ReturnsValid()
    {
        // The per-turn re-validation path. An over-cap message here is a message the node already stored, so failing the
        // package would fail every remaining turn of that conversation — the poisoning this carve-out removes. The
        // context budgeter trims oversized history downstream.
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithUserMessage(new string(c: 'b', count: 1025)).Build(),
            enforceMessageSizeCap: false);

        AssertEx.True(result.IsValid);
        AssertEx.Empty(result.Errors);
    }

    [Test]
    public void Validate_WhenMessageContainsNullByte_AndTheSizeCapIsNotEnforced_StillReturnsError()
    {
        // The carve-out is scoped to the SIZE cap only: a null byte is an integrity fault whoever produced it.
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithUserMessage("abc\0def").Build(), enforceMessageSizeCap: false);

        AssertErrorContains(result, "conversation message");
    }

    [Test]
    public void Validate_WhenMessageContainsNullByte_ReturnsError()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithUserMessage("abc\0def").Build());

        AssertErrorContains(result, "conversation message");
    }

    [Test]
    public void Validate_WhenConversationContextIsNull_ReturnsError()
    {
        var package = RuntimePackageBuilder.Valid().Build() with
        {
            ConversationContext = null!
        };

        var exception = AssertEx.ThrowsAsync<ArgumentNullException>(() => Task.FromResult(_validator.Validate(package))).GetAwaiter().GetResult();
        AssertEx.Equal("conversationContext", exception.ParamName);
    }

    [Test]
    public void Validate_WhenInvocationTimeoutIsBelowMinimum_ReturnsError()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithTimeout(0).Build());

        AssertErrorContains(result, "timeout");
    }

    [Test]
    public void Validate_WhenToolCallTimeoutIsBelowMinimum_ReturnsError()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithTimeout(toolCallSeconds: 0).Build());

        AssertErrorContains(result, "timeout");
    }

    [Test]
    public void Validate_WhenConfigHashIsEmpty_ReturnsError()
    {
        var result = _validator.Validate(RuntimePackageBuilder.Valid().WithConfigHash(string.Empty).Build());

        AssertErrorContains(result, "integrity");
    }

    [Test]
    public void Validate_WhenAllowedToolHasBlankName_ReturnsError()
    {
        var package = RuntimePackageBuilder.Valid().Build() with
        {
            AllowedTools =
            [
                new AllowedToolDto
                {
                    Id = Guid.NewGuid(),
                    Name = string.Empty,
                    Location = ToolLocation.ApiSide
                }
            ]
        };

        var result = _validator.Validate(package);

        AssertErrorContains(result, "tool definition");
    }

    [Test]
    public void Validate_WhenMultipleFieldsAreInvalid_ReturnsAllErrors()
    {
        var package = RuntimePackageBuilder.Valid().Build() with
        {
            ResolvedSystemPrompt = "<script>alert(1)</script>",
            ModelProfile = "foo/bar",
            ConfigHash = string.Empty,
            ConversationContext =
            [
                new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = "bad\0message",
                    SortOrder = 0
                }
            ],
            AllowedTools =
            [
                new AllowedToolDto
                {
                    Id = Guid.NewGuid(),
                    Name = string.Empty,
                    Location = ToolLocation.ApiSide
                }
            ],
            Timeouts = new TimeoutSettings
            {
                InvocationTimeoutSeconds = 0,
                ToolCallTimeoutSeconds = 0,
                StreamIdleTimeoutSeconds = 0
            }
        };

        var result = _validator.Validate(package);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Count >= 5);
    }

    [Test]
    public void Validate_WhenMessageHasBlankContentButImageParts_IsValid()
    {
        // A vision (image-only) turn carries blank text — the image parts are its payload — and must NOT be rejected as
        // blank-content. Regression guard: the validator previously flagged every whitespace-only message.
        var package = RuntimePackageBuilder.Valid().Build() with
        {
            ConversationContext =
            [
                new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = string.Empty,
                    SortOrder = 0,
                    Images =
                    [
                        new ConversationImagePart("image/png", new byte[]
                        {
                            0x89,
                            0x50,
                            0x4E,
                            0x47
                        })
                    ]
                }
            ]
        };

        var result = _validator.Validate(package);

        AssertEx.True(result.IsValid);
    }

    [Test]
    public void Validate_WhenMessageHasBlankContentButToolExchanges_IsValid()
    {
        // The same exemption images have, for the same reason: a caller-managed turn that called a tool and then died
        // has no text of its own, and its replayed exchanges ARE its payload. Without this the validator would reject
        // exactly the turn the replay exists to carry.
        var package = RuntimePackageBuilder.Valid().Build() with
        {
            ConversationContext =
            [
                new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.Assistant,
                    Content = "   ",
                    SortOrder = 0,
                    ToolExchanges = [new ConversationToolExchange("call-1", "save_artifact", "{}", "saved", IsError: false)]
                }
            ]
        };

        var result = _validator.Validate(package);

        AssertEx.True(result.IsValid);
    }

    [Test]
    public void Validate_WhenMessageHasBlankContentAndNoImages_IsInvalid()
    {
        // The image carve-out must not loosen the blank-content fault for an ordinary text message with no image parts.
        var package = RuntimePackageBuilder.Valid().Build() with
        {
            ConversationContext =
            [
                new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = "   ",
                    SortOrder = 0
                }
            ]
        };

        var result = _validator.Validate(package);

        AssertErrorContains(result, "conversation message content");
    }

    [Test]
    [Arguments("on")]
    [Arguments("On")]
    [Arguments("ON")]
    public void Validate_WhenReasoningEffortIsBinaryOn_ReturnsValid(string reasoningEffort)
    {
        var package = RuntimePackageBuilder.Valid().Build() with
        {
            ReasoningEffort = reasoningEffort
        };

        var result = _validator.Validate(package);

        AssertEx.True(result.IsValid);
        AssertEx.False(result.Errors.Any(error => error.Contains("reasoning effort", StringComparison.OrdinalIgnoreCase)));
    }

    [Test]
    public void Validate_WhenReasoningEffortIsInvalid_ReturnsError()
    {
        var package = RuntimePackageBuilder.Valid().Build() with
        {
            ReasoningEffort = "bogus"
        };

        var result = _validator.Validate(package);

        AssertErrorContains(result, "reasoning effort");
    }

    private static void AssertErrorContains(RuntimePackageValidationResult result, string expectedText)
    {
        AssertEx.False(result.IsValid);
        AssertEx.NotEmpty(result.Errors);
        AssertEx.Contains(result.Errors, error => error.Contains(expectedText, StringComparison.OrdinalIgnoreCase));
    }
}
