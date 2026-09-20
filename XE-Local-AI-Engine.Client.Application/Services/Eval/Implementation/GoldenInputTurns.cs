namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
///     Shared parse and strict validation of a golden case's stored <c>InputTurns</c> JSON, used by BOTH the
///     create-time validation and the eval-time scoring path, so the two agree on what a usable conversation is.
/// </summary>
/// <remarks>
///     A turn is valid only with a KNOWN role and non-blank text; an unknown role is rejected rather than collapsed
///     to <c>User</c>, which would reshape the evaluated conversation, and a case with no valid turns is unusable.
///     Validation applies at create and update, while a stored legacy row that fails these rules reads fine and
///     degrades to an explicit failed case at eval time, never a silent pass on the system prompt alone.
/// </remarks>
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
            // A JSON null element deserializes to a null record, so it counts as malformed rather than dereferencing
            // into a NullReferenceException that would escape the per-case loop and surface as an unhandled 500.
            if (turn is null)
            {
                error = "InputTurns contains a null turn.";
                return false;
            }

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
