namespace XE_Local_AI_Engine.Tests.E2ETests.Common;

using TUnit.Core.Interfaces;

/// <summary>
///     Caps concurrent browser-backed E2E tests so a single test session does not spawn an
///     unbounded number of Chromium instances. Mirrors the C0re harness limit.
/// </summary>
public sealed class BrowserParallelLimit : IParallelLimit
{
    public int Limit => 2;
}
