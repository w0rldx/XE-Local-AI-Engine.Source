namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

/// <summary>
///     Turns an absent blob column into an absent <see cref="ReadOnlyMemory{T}" />, which needs spelling out as two
///     statements.
/// </summary>
/// <remarks>
///     <c>ReadOnlyMemory&lt;byte&gt;</c> has an implicit operator from <c>byte[]</c>, so a null array — and even a bare
///     <c>null</c> inside a conditional expression — converts to an EMPTY memory rather than to a null
///     <see cref="Nullable{T}" />. Both <c>value?.ToArray()</c> and <c>value is null ? null : …</c> silently produce
///     <c>HasValue == true</c> with <c>Length == 0</c>; only a plain <c>return null;</c> against the nullable return
///     type does not. Every store that projects a nullable blob column onto a record goes through here so the mistake
///     is fixed once rather than re-made per projection.
/// </remarks>
internal static class OptionalBlob
{
    public static ReadOnlyMemory<byte>? AsOptionalMemory(byte[]? value)
    {
        if (value is null)
        {
            return null;
        }

        return new ReadOnlyMemory<byte>(value);
    }
}
