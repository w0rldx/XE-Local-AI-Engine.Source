[assembly: ParallelLimiter<XE_Local_AI_Engine.Client.Persistence.Tests.AssemblyParallelLimit>]

namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using TUnit.Core.Interfaces;

/// <summary>
///     Caps concurrent tests in this assembly at eight whatever launched it: at TUnit's default width of 4 × CPU
///     cores this module measured 11.6 GB RSS, against 2.7 GB at the gate's width of four.
/// </summary>
/// <remarks>The gate still passes its own narrower <c>--maximum-parallel-tests</c>; this is the ceiling a bare run cannot exceed.</remarks>
public sealed class AssemblyParallelLimit : IParallelLimit
{
    public int Limit => 8;
}
