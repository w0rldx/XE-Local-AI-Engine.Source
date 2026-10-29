namespace XE_Local_AI_Engine.Tests.Integrations;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     Test-only reads over <see cref="IntegrationAcceptResult" />: the accept tests compare the rows two calls named, and
///     a rejection simply names none.
/// </summary>
internal static class IntegrationAcceptResultTestView
{
    extension(IntegrationAcceptResult result)
    {
        public Guid? ExecutionId => (result as IntegrationAcceptResult.Admitted)?.ExecutionId;

        public Guid? SessionId => (result as IntegrationAcceptResult.Admitted)?.SessionId;

        public IntegrationExecutionStatus? Status => (result as IntegrationAcceptResult.Admitted)?.Status;
    }
}
