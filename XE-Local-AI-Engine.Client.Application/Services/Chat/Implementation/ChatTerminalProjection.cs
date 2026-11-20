namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     The failure category a terminal turn shows in the chat, shared by every writer that projects a turn onto its message
///     (live delivery, reconnect replay, the persisted row) so the three cannot disagree.
/// </summary>
internal static class ChatTerminalProjection
{
    /// <summary>
    ///     The category of a failed turn, or of a cancel the user did not ask for (<see cref="InvocationState.ShowCancelReason" />);
    ///     none for a plain Stop, an outcome the user chose. Pairs with <see cref="InvocationState.ChatError" />.
    /// </summary>
    public static FailureCategory? ChatFailureCategory(InvocationState state) =>
        state.Status == InvocationStatus.Failed || (state.Status == InvocationStatus.Cancelled && state.ShowCancelReason)
            ? state.FailureCategory
            : null;
}
