[assembly: ParallelLimiter<XE_Local_AI_Engine.Cli.Tests.AssemblyParallelLimit>]

namespace XE_Local_AI_Engine.Cli.Tests;

using TUnit.Core.Interfaces;

/// <summary>
///     Caps concurrent tests in this assembly at eight whatever launched it, as the other in-process host suites do:
///     TUnit's default width is 4 × CPU cores, and each integration test here starts a whole node.
/// </summary>
/// <remarks>The gate still passes its own <c>--maximum-parallel-tests</c>; this is the ceiling a bare run cannot exceed.</remarks>
public sealed class AssemblyParallelLimit : IParallelLimit
{
    public int Limit => 8;
}
