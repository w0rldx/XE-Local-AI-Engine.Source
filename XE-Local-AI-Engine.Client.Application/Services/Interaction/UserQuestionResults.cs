namespace XE_Local_AI_Engine.Client.Services.Interaction;

using System.Text.Json;

/// <summary>
///     Builds the MODEL-visible result of an <c>ask_user</c> call. Centralised here because two seams produce it — the
///     runner (which owns the human round-trip and therefore every not-answered outcome) and the tool handler (which
///     returns the stashed value, or the fail-safe when nothing was stashed) — and a model that has to branch on the
///     result cannot afford the two to drift.
///     <para>
///         Every shape carries <c>answered</c>, so "the user chose nothing" and "the user was never asked" are never
///         confusable: an empty <c>selected</c> under <c>answered:true</c> is a deliberate empty choice, whereas
///         <c>answered:false</c> always names a <c>reason</c> and carries a plain sentence telling the model what to do
///         next. Small models branch far more reliably on a present boolean than on the absence of a field.
///     </para>
/// </summary>
internal static class UserQuestionResults
{
    /// <summary>The wait ran and the operator did not answer before the pending-question cap elapsed.</summary>
    public const string TimeoutReason = "timeout";

    /// <summary>The model's arguments could not be parsed or validated, so the operator was never shown a prompt.</summary>
    public const string MalformedCallReason = "malformed_call";

    /// <summary>The tool executed with no stashed answer — a defect or a torn-down turn, never a user action.</summary>
    public const string NotCollectedReason = "not_collected";

    /// <summary>
    ///     The run is unattended (scheduled/headless), so there is nobody to prompt and the question was never shown.
    ///     Distinct from <see cref="TimeoutReason" />: no wait happened at all, and telling the model it timed out when
    ///     the prompt was never displayed would be a lie it might act on by re-asking. Falls through to the generic
    ///     proceed-on-your-own message below, which is exactly the guidance this case needs.
    /// </summary>
    public const string UnattendedReason = "unattended";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private const string ProceedInstruction =
        "Continue with your own best judgement and state plainly which assumption you made.";

    /// <summary>
    ///     The result for a completed round-trip. <c>other</c> is emitted even when null so the model sees one stable
    ///     answer shape rather than a key that appears and disappears.
    /// </summary>
    public static string Answered(IReadOnlyList<UserQuestionAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        return JsonSerializer.Serialize(new
        {
            answered = true,
            answers = answers.Select(static answer => new { question = answer.Question, selected = answer.Selected, other = answer.Other })
        }, SerializerOptions);
    }

    /// <summary>
    ///     The result for a round-trip that produced no answer (decision D4: the turn must continue, not fail). The
    ///     sentence is deliberately directive — a bare "no answer" leaves a small model stalling or re-asking.
    /// </summary>
    public static string Unanswered(string reason, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var message = reason switch
        {
            MalformedCallReason =>
                $"Your ask_user call could not be shown to the user because its arguments were invalid: {detail} "
                + "Either call ask_user again with corrected arguments, or continue without asking.",
            TimeoutReason => $"The user did not answer in time. {ProceedInstruction}",
            _ => $"No answer was collected, so the user was never asked. {ProceedInstruction}"
        };

        return JsonSerializer.Serialize(new { answered = false, reason, message }, SerializerOptions);
    }
}
