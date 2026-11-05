namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Default <see cref="IProcessVramBudgetProbe" /> that always reports "unknown" (<see langword="null" />). Wired via
///     <c>TryAddSingleton</c> so callers degrade when the real <c>--list-devices</c>-backed probe is unavailable.
/// </summary>
public sealed class UnknownProcessVramBudgetProbe : IProcessVramBudgetProbe
{
    /// <inheritdoc />
    public Task<long?> GetProcessBudgetBytesOrNullAsync(string backend, CancellationToken ct)
    {
        return Task.FromResult<long?>(null);
    }
}
