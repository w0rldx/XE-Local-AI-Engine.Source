[assembly: ParallelLimiter<XE_Local_AI_Engine.Tests.AssemblyParallelLimit>]

namespace XE_Local_AI_Engine.Tests;

using TUnit.Core.Interfaces;

/// <summary>
///     Caps concurrent tests in this assembly at eight whatever launched it: TUnit's default is 4 × CPU cores, and
///     every live <see cref="TestServerWebAppFactory" /> host costs ~200 MB.
/// </summary>
/// <remarks>
///     Uncapped, a bare run on a many-core machine reached 128 concurrent tests and 23 GB RSS; capped, the
///     <c>Endpoints</c> namespaces alone still peak near 8.5 GB because shared per-class hosts outlive their tests, so
///     the memory-safe runner stays the tool of record. A <c>--maximum-parallel-tests</c> below eight still narrows.
/// </remarks>
public sealed class AssemblyParallelLimit : IParallelLimit
{
    public int Limit => 8;
}
