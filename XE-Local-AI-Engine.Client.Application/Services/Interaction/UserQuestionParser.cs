namespace XE_Local_AI_Engine.Client.Services.Interaction;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
///     Parses and validates the arguments of an <c>ask_user</c> tool call into the questions the chat card renders.
///     <para>
///         WHY this validation lives here and not only in the tool handler: <c>ask_user</c> is approval-required, so the
///         runner intercepts the call BEFORE <c>ToolArgumentRepairAIFunction</c> — the wrapper that normally coerces and
///         repairs model arguments — ever sees it. The runner is therefore the first and only guard between raw model
///         output and something shown to a human, and it must be able to reject a malformed call without prompting.
///     </para>
/// </summary>
internal static class UserQuestionParser
{
    // Mirrors the bounds declared in AskUserTool.ParameterSchema. Kept small on purpose (the schema is compiled into
    // llama.cpp's combined GBNF grammar); enforced here because a schema bound is advisory to the model, not binding.
    private const int MaxQuestions = 4;
    private const int MinOptions = 2;
    private const int MaxOptions = 6;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Projects the framework's argument bag into validated <see cref="UserQuestionSpec" />s. Returns
    ///     <see langword="false" /> with a short, content-free <paramref name="error" /> when the call cannot be shown to
    ///     the operator; the caller turns that into a "your call was malformed" tool result rather than a prompt.
    /// </summary>
    public static bool TryParse(IDictionary<string, object?>? arguments,
        [NotNullWhen(true)] out IReadOnlyList<UserQuestionSpec>? questions,
        [NotNullWhen(false)] out string? error)
    {
        questions = null;

        if (arguments is null || arguments.Count == 0)
        {
            error = "the call carried no arguments.";
            return false;
        }

        AskUserArguments? parsed;
        try
        {
            // Round-tripping through an element rather than reading the bag by hand keeps this correct for every shape
            // a provider hands back — JsonElement values, boxed primitives, or already-materialised objects.
            parsed = JsonSerializer.Deserialize<AskUserArguments>(JsonSerializer.SerializeToElement(arguments, SerializerOptions), SerializerOptions);
        }
        catch (JsonException exception)
        {
            error = $"the arguments were not valid JSON ({exception.Message}).";
            return false;
        }

        if (parsed?.Questions is not { Count: > 0 } parsedQuestions)
        {
            error = "\"questions\" was missing or empty; it must contain at least one question.";
            return false;
        }

        if (parsedQuestions.Count > MaxQuestions)
        {
            error = $"\"questions\" carried {parsedQuestions.Count} entries; at most {MaxQuestions} are allowed.";
            return false;
        }

        var specs = new List<UserQuestionSpec>(parsedQuestions.Count);
        foreach (var question in parsedQuestions)
        {
            if (!TryProjectQuestion(question, out var spec, out error))
            {
                return false;
            }

            specs.Add(spec);
        }

        questions = specs;
        error = null;
        return true;
    }

    private static bool TryProjectQuestion(AskUserQuestion question,
        [NotNullWhen(true)] out UserQuestionSpec? spec,
        [NotNullWhen(false)] out string? error)
    {
        spec = null;

        if (string.IsNullOrWhiteSpace(question.Question))
        {
            error = "a question had an empty \"question\" text.";
            return false;
        }

        if (question.Options is not { Count: >= MinOptions and <= MaxOptions } options)
        {
            error = $"each question needs between {MinOptions} and {MaxOptions} options.";
            return false;
        }

        var projected = new List<UserQuestionOption>(options.Count);
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Label))
            {
                error = "an option had an empty \"label\".";
                return false;
            }

            projected.Add(new UserQuestionOption(option.Label.Trim(),
                string.IsNullOrWhiteSpace(option.Description) ? null : option.Description.Trim(),
                option.Recommended ?? false));
        }

        spec = new UserQuestionSpec(question.Header?.Trim() ?? string.Empty,
            question.Question.Trim(),
            question.MultiSelect ?? false,
            projected);
        error = null;
        return true;
    }

    private sealed record AskUserArguments(IReadOnlyList<AskUserQuestion>? Questions);

    private sealed record AskUserQuestion(string? Header, string? Question, bool? MultiSelect, IReadOnlyList<AskUserOption>? Options);

    private sealed record AskUserOption(string? Label, string? Description, bool? Recommended);
}
