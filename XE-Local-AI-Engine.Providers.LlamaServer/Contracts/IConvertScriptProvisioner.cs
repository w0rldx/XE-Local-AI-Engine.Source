namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The llama.cpp conversion tooling a training export runs, provisioned at a pinned commit under the managed
///     runtime directory.
/// </summary>
/// <param name="HfToGgufScriptPath">Absolute path to <c>convert_hf_to_gguf.py</c> (merged fine-tune → GGUF).</param>
/// <param name="LoraToGgufScriptPath">Absolute path to <c>convert_lora_to_gguf.py</c> (LoRA adapter → GGUF).</param>
/// <param name="GgufPyDirectory">
///     Absolute path to the <c>gguf-py</c> package directory both scripts import. Put this on <c>PYTHONPATH</c> for the
///     conversion subprocess — the scripts resolve the package relative to the repository they normally live in, which
///     this provisioned tree deliberately is not.
/// </param>
/// <param name="SourceCommit">The verified upstream commit the three paths were taken from.</param>
public sealed record ConvertScriptPaths(
    string HfToGgufScriptPath,
    string LoraToGgufScriptPath,
    string GgufPyDirectory,
    string SourceCommit);

/// <summary>
///     Acquires and adopts the llama.cpp conversion scripts at the exact commit the installed inference runtime is
///     pinned to (<see cref="LlamaCppReleasePins.PinnedSourceCommitSha" />), so a converted GGUF is guaranteed readable
///     by the server that will load it.
/// </summary>
/// <remarks>
///     The adopted tree is independent of any build work directory — the scripts survive a completed, cancelled, or
///     never-run source build, and are provisioned on a host that only ever used prebuilt binaries.
/// </remarks>
public interface IConvertScriptProvisioner
{
    /// <summary>
    ///     Returns the already-provisioned scripts for the pinned commit, or <see langword="null" /> when nothing has
    ///     been adopted yet. Pure disk check; never fetches.
    /// </summary>
    ConvertScriptPaths? TryResolve();

    /// <summary>
    ///     Returns the provisioned scripts, acquiring and adopting them first when absent. Single-flight: concurrent
    ///     callers share one acquisition. Adoption is atomic — a partial fetch is never visible to
    ///     <see cref="TryResolve" />.
    /// </summary>
    /// <exception cref="LlamaRuntimeException">Acquisition failed, or the fetched tree did not match the pinned commit.</exception>
    Task<ConvertScriptPaths> EnsureAsync(CancellationToken ct);
}

/// <summary>
///     Fetches the llama.cpp source tree at an exact commit into a caller-owned directory. Test seam: the provisioner's
///     adopt/verify logic is exercised against a fake that writes a fixed tree instead of reaching the network.
/// </summary>
public interface IConvertScriptSourceFetcher
{
    /// <summary>
    ///     Populates <paramref name="destinationDirectory" /> with the repository content at
    ///     <paramref name="commitSha" /> and returns the commit actually checked out. A return value other than
    ///     <paramref name="commitSha" /> is treated as a provenance failure by the caller.
    /// </summary>
    Task<string> FetchAsync(string destinationDirectory, string commitSha, CancellationToken ct);
}
