namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Probes the host for the toolchain an in-app source build of a CUDA <c>llama-server</c> requires. The probe
///     installs nothing — it only reports, item by item, what is present and what is missing, so the UI can offer an
///     itemized checklist and enable the build button only when every item is satisfied on a Linux host.
/// </summary>
/// <remarks>
///     The in-app CUDA build is the no-build-knowledge alternative to the operator bring-your-own override: upstream
///     ships no prebuilt Linux CUDA asset, so source-build is the only route to CUDA for a non-expert Linux user. The
///     probe is Linux-only: on any other OS <see cref="CudaBuildPrerequisiteReport.CanBuild" /> is <see langword="false" />
///     with the OS item unsatisfied and the tool items short-circuited.
/// </remarks>
public interface ICudaBuildPrerequisiteProbe
{
    /// <summary>Probes every prerequisite and returns the itemized report (degrades to "not satisfied", never throws).</summary>
    Task<CudaBuildPrerequisiteReport> ProbeAsync(CancellationToken ct);
}

/// <summary>
///     One prerequisite checklist row. <see cref="Detail" /> is a short, sanitized, user-safe note (a version banner or a
///     "not found" reason) — never an absolute path, URL, or secret.
/// </summary>
/// <param name="Key">Stable item key (for example <c>os-is-linux</c>, <c>nvcc</c>, <c>free-disk</c>).</param>
/// <param name="Satisfied">Whether this item is present/sufficient.</param>
/// <param name="Detail">A short sanitized note describing the result.</param>
public sealed record CudaBuildPrerequisiteItem(string Key, bool Satisfied, string Detail);

/// <summary>
///     The full prerequisite report: every checklist <see cref="Items" /> plus the overall <see cref="CanBuild" />
///     gate (true only when the host is Linux and every item is satisfied).
/// </summary>
/// <param name="CanBuild">True only when the OS is Linux and every <see cref="Items" /> entry is satisfied.</param>
/// <param name="Items">The itemized checklist, in display order.</param>
public sealed record CudaBuildPrerequisiteReport(bool CanBuild, IReadOnlyList<CudaBuildPrerequisiteItem> Items);
