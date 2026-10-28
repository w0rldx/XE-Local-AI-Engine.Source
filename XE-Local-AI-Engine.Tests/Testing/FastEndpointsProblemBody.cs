namespace XE_Local_AI_Engine.Tests.Testing;

using FastEndpoints;

/// <summary>
///     The value <c>errors[0].name</c> carries in a problem body written by <c>FastEndpointsProblemWriter</c>:
///     FastEndpoints' <c>ErrorOptions.GeneralErrorsField</c> default put through the naming policy its
///     <c>ProblemDetails</c> constructor applies.
/// </summary>
/// <remarks>
///     Never collapse a caller to a <c>"GeneralErrors"</c> literal: <c>FastEndpoints.Config</c> is process-global
///     (hence <c>new Config()</c>), so a handler over a bare <c>DefaultHttpContext</c> sees the policy the first host
///     left (none, or camelCase once any host ran <c>UseFastEndpoints</c>). A literal asserts test ORDER: red at
///     <c>--maximum-parallel-tests 1</c>, green at default width. Deriving the value from the writer's policy still
///     fails a writer that skips the policy or moves the message to another field.
/// </remarks>
internal static class FastEndpointsProblemBody
{
    private const string GeneralErrorsField = "GeneralErrors";

    public static string GeneralErrorsName => new Config().Serializer.Options.PropertyNamingPolicy?.ConvertName(GeneralErrorsField) ?? GeneralErrorsField;
}
