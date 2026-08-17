namespace XE_Local_AI_Engine.Client.Services.Models;

public interface ILocalModelDeletionJournalReconciler
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
