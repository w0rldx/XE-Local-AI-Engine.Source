namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Validators;

using System.Globalization;
using System.Linq.Expressions;
using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Models;

/// <summary>Bounds every stored sampling knob to the range the chat's sampling dialog offers; a null knob is unset.</summary>
/// <remarks>
///     Rules use <c>Must</c>, never range rules: the conversation RESPONSE shares the settings type, and FastEndpoints
///     would publish range rules as schema bounds the browser then enforces when it loads an older chat.
/// </remarks>
public sealed class SetNodeChatConversationSamplingOptionsRequestValidator : Validator<SetNodeChatConversationSamplingOptionsRequest>
{
    public const int MaxContextTokens = 131_072;
    public const int MaxStopSequences = 16;
    public const int MaxStopSequenceLength = 256;

    public SetNodeChatConversationSamplingOptionsRequestValidator()
    {
        RuleFor(static request => request.ConversationId)
            .NotEmpty();

        When(static request => request.SamplingOptions is not null, () =>
        {
            Range(static request => request.SamplingOptions!.Temperature, 0f, 2f);
            Range(static request => request.SamplingOptions!.TopP, 0f, 1f);
            Range(static request => request.SamplingOptions!.TopK, 1, 200);
            Range(static request => request.SamplingOptions!.MinP, 0f, 1f);
            Range(static request => request.SamplingOptions!.MaxOutputTokens, 1, MaxContextTokens);
            Range(static request => request.SamplingOptions!.RepeatPenalty, 0f, 2f);
            Range(static request => request.SamplingOptions!.RepeatLastN, -1, 512);
            Range(static request => request.SamplingOptions!.PresencePenalty, -2f, 2f);
            Range(static request => request.SamplingOptions!.FrequencyPenalty, -2f, 2f);
            Range(static request => request.SamplingOptions!.NumCtx, 512, MaxContextTokens);
            RuleFor(static request => request.SamplingOptions!.Seed)
                .Must(static seed => SeedValue.TryParse(seed, out var parsed, out _) && parsed is null or (>= -1 and <= int.MaxValue))
                .WithMessage($"The seed must be a whole number from -1 to {int.MaxValue}.");
            RuleFor(static request => request.SamplingOptions!.Stop)
                .Must(static stop => stop is null || stop.Count <= MaxStopSequences && stop.All(static sequence => sequence is { Length: > 0 and <= MaxStopSequenceLength }))
                .WithMessage($"Send at most {MaxStopSequences} stop sequences, each 1 to {MaxStopSequenceLength} characters long.");
        });
    }

    private void Range<T>(Expression<Func<SetNodeChatConversationSamplingOptionsRequest, T?>> knob, T min, T max)
        where T : struct, IComparable<T>
    {
        RuleFor(knob)
            .Must(value => value is not { } set || set.CompareTo(min) >= 0 && set.CompareTo(max) <= 0)
            .WithMessage(string.Create(CultureInfo.InvariantCulture, $"'{{PropertyName}}' must be from {min} to {max}."));
    }
}
