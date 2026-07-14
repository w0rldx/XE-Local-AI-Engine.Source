namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
///     Shared parse + strict validation of a golden case's stored <c>InputTurns</c> JSON, used by BOTH the
///     create/update validation path (<see cref="GoldenConversationService" />) and the eval-time scoring path
///     (<see cref="PlaybookEvalService" />) so the two agree on what a usable conversation is. A turn is valid only when
///     it carries a KNOWN role (<c>user</c>/<c>assistant</c>) and non-blank text; an unknown role is rejected outright
///     rather than silently collapsed to <c>User</c> (which would reshape the evaluated conversation), and a case with no
///     valid turns is unusable. Validation applies at create/update; a stored legacy row that fails these rules is read
///     fine and degrades to an explicit failed case at eval time — never a silent pass on the system prompt alone.
/// </summary>
internal static class GoldenInputTurns
{
    internal const string UserRole = "user";
    internal const string AssistantRole = "assistant";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Parses and validates the stored turns. On success <paramref name="messages" /> holds the mapped
    ///     <see cref="ChatMessage" /> conversation and <paramref name="error" /> is <see langword="null" />. On failure
    ///     (malformed JSON, no turns, an unknown role, or a blank-text turn) returns <see langword="false" /> with a
    ///     human-readable <paramref name="error" /> and an empty message list.
    /// </summary>
    internal static bool TryParse(string? inputTurnsJson, out IReadOnlyList<ChatMessage> messages, out string? error)
    {
        messages = [];

        RawTurn[]? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawTurn[]>(inputTurnsJson ?? string.Empty, SerializerOptions);
        }
        catch (JsonException exception)
        {
            error = $"InputTurns is not valid JSON: {exception.Message}";
            return false;
        }

        if (raw is null || raw.Length == 0)
        {
            error = "InputTurns must contain at least one turn.";
            return false;
        }

        var mapped = new List<ChatMessage>(raw.Length);
        foreach (var turn in raw)
        {
            if (!TryMapRole(turn.Role, out var role))
            {
                error = $"InputTurns contains an unknown role '{turn.Role}'. Allowed roles are '{UserRole}' and '{AssistantRole}'.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(turn.Text))
            {
                error = "InputTurns contains a turn with blank text.";
                return false;
            }

            mapped.Add(new ChatMessage(role, turn.Text));
        }

        messages = mapped;
        error = null;
        return true;
    }

    private static bool TryMapRole(string? role, out ChatRole mapped)
    {
        if (string.Equals(role, UserRole, StringComparison.OrdinalIgnoreCase))
        {
            mapped = ChatRole.User;
            return true;
        }

        if (string.Equals(role, AssistantRole, StringComparison.OrdinalIgnoreCase))
        {
            mapped = ChatRole.Assistant;
            return true;
        }

        mapped = default;
        return false;
    }

    // Positional record: System.Text.Json binds JSON properties to the constructor parameters by name (Web defaults).
    private sealed record RawTurn(string? Role, string? Text);
}
