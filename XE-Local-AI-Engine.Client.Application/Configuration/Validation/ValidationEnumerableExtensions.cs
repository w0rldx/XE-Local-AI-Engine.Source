namespace XE_Local_AI_Engine.Client.Configuration.Validation;

internal static class ValidationEnumerableExtensions
{
    public static IEnumerable<string> AppendIf(this IEnumerable<string> source,
        bool condition,
        string error)
    {
        return condition ? source.Append(error) : source;
    }
}
