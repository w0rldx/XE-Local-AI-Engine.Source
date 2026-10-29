namespace XE_Local_AI_Engine.Tests.Integrations;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     Test-only reads over <see cref="IntegrationTriggerResult" />: a saved write carries its row and no refusal, a
///     refused one the reverse.
/// </summary>
internal static class IntegrationTriggerResultTestView
{
    extension(IntegrationTriggerResult result)
    {
        public IntegrationTriggerOutcome? Outcome => (result as IntegrationTriggerResult.Rejected)?.Outcome;

        public string? Message => (result as IntegrationTriggerResult.Rejected)?.Message;

        public IntegrationTriggerSnapshot? Trigger => (result as IntegrationTriggerResult.Saved)?.Trigger;
    }
}
