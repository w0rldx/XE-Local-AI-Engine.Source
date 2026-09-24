namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

internal static class NodeManagedLlamaRoutingScope
{
    private static readonly AsyncLocal<string?> RequiredModel = new();

    public static string? CurrentModel => RequiredModel.Value;

    public static IDisposable Begin(string? model)
    {
        var prior = RequiredModel.Value;
        RequiredModel.Value = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        return new Scope(prior);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _prior;

        public Scope(string? prior)
        {
            _prior = prior;
        }

        public void Dispose() =>
            RequiredModel.Value = _prior;
    }
}
