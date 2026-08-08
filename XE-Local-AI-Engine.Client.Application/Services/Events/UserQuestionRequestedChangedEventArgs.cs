namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Carries a pending <c>ask_user</c> question out to the local chat stream, which surfaces it as a
///     <c>question-requested</c> stream event. Mirrors <see cref="ApprovalRequestedChangedEventArgs" />.
/// </summary>
public sealed class UserQuestionRequestedChangedEventArgs : EventArgs
{
    public UserQuestionRequestedChangedEventArgs(UserQuestionLifecyclePayload payload)
    {
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public UserQuestionLifecyclePayload Payload { get; }
}
