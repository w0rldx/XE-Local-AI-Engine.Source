namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>An installed GGUF that stands in for a base checkpoint: the model an adapter is served against and the base side of a comparison.</summary>
public sealed record InstalledBaseModelLink(string ModelName, string RepoId, string? ContentFingerprint);

/// <summary>
///     Resolves the installed GGUF counterpart of a base checkpoint repo. Live-found (2026-08-15): nothing wrote
///     <c>LinkedInstalledModelName</c> on a run, so an adapter export could never be smoke-tested or promoted and a
///     comparison had no base side. The wizard may name a model explicitly; otherwise the Hugging Face convention that
///     the official quantized repo is <c>&lt;base&gt;-GGUF</c> (or the same repo id) picks it, and a miss stays a
///     miss — never a guess by display name.
/// </summary>
public interface IInstalledBaseModelLinker
{
    /// <summary>The installed models that can stand in for <paramref name="baseRepoId" />, best match first.</summary>
    Task<IReadOnlyList<InstalledBaseModelLink>> SuggestAsync(string baseRepoId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves the link for a run: <paramref name="explicitModelName" /> when given (it must be installed), else the
    ///     first suggestion, else <see langword="null" />.
    /// </summary>
    /// <exception cref="TrainingRunRejectedException">An explicitly named model is not installed.</exception>
    Task<InstalledBaseModelLink?> ResolveAsync(string baseRepoId, string? explicitModelName, CancellationToken cancellationToken = default);
}

public sealed class InstalledBaseModelLinker(IGgufModelRegistry registry) : IInstalledBaseModelLinker
{
    private readonly IGgufModelRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task<IReadOnlyList<InstalledBaseModelLink>> SuggestAsync(string baseRepoId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRepoId);
        var entries = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        return entries
               .Where(entry => MatchesBase(entry.RepoId, baseRepoId))
               // Adapter entries are themselves derived from a base and cannot serve as one.
               .Where(entry => string.IsNullOrEmpty(entry.BaseModelName))
               .OrderBy(entry => string.Equals(entry.RepoId, baseRepoId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
               .ThenBy(entry => entry.ModelName, StringComparer.Ordinal)
               .Select(entry => new InstalledBaseModelLink(entry.ModelName, entry.RepoId, entry.ModelContentFingerprint))
               .ToArray();
    }

    public async Task<InstalledBaseModelLink?> ResolveAsync(string baseRepoId, string? explicitModelName, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitModelName))
        {
            var entry = await _registry.FindAsync(explicitModelName, cancellationToken).ConfigureAwait(false)
                        ?? throw new TrainingRunRejectedException($"'{explicitModelName}' is not an installed model.");
            return new InstalledBaseModelLink(entry.ModelName, entry.RepoId, entry.ModelContentFingerprint);
        }

        var suggestions = await SuggestAsync(baseRepoId, cancellationToken).ConfigureAwait(false);
        return suggestions.Count > 0 ? suggestions[0] : null;
    }

    private static bool MatchesBase(string candidateRepoId, string baseRepoId)
    {
        if (string.Equals(candidateRepoId, baseRepoId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Official quantized repos follow "<owner>/<model>-GGUF"; community ones do not, and are deliberately not guessed.
        return candidateRepoId.Length == baseRepoId.Length + 5
               && candidateRepoId.StartsWith(baseRepoId, StringComparison.OrdinalIgnoreCase)
               && candidateRepoId.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase);
    }
}
