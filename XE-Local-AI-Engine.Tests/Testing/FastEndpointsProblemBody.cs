namespace XE_Local_AI_Engine.Tests.Testing;

using FastEndpoints;

/// <summary>
///     The value <c>errors[0].name</c> carries in a problem body written by <c>FastEndpointsProblemWriter</c>:
///     FastEndpoints' <c>ErrorOptions.GeneralErrorsField</c> default put through the naming policy its
///     <c>ProblemDetails</c> constructor applies.
///     <para>
///         Do NOT collapse a caller back to a <c>"GeneralErrors"</c> literal. <c>FastEndpoints.Config</c> is backed by
///         process-global statics (its instance properties read them, which is why this reaches them through
///         <c>new Config()</c>), so a handler driven over a bare <c>DefaultHttpContext</c> sees whichever policy the
///         first host booted in that process left behind — none of its own, camelCase once any host has run
///         <c>UseFastEndpoints</c>. A literal therefore asserts test ORDER: it made the bare-context assertions red at
///         <c>--maximum-parallel-tests 1</c> and green at default width. Deriving the expected value from the same
///         policy the writer reads keeps the assertion honest — a writer that stopped applying the policy, or hung the
///         message off a different field, still fails.
///     </para>
/// </summary>
internal static class FastEndpointsProblemBody
{
    private const string GeneralErrorsField = "GeneralErrors";

    public static string GeneralErrorsName => new Config().Serializer.Options.PropertyNamingPolicy?.ConvertName(GeneralErrorsField) ?? GeneralErrorsField;
}
