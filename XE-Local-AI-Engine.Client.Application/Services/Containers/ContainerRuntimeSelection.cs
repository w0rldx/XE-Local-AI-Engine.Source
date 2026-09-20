namespace XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>
///     Which container runtime the node uses for application containers. Both values resolve to Docker in V1; the enum
///     exists so that adding a second provider is a change to the resolver rather than to every caller.
/// </summary>
public enum ContainerRuntimeSelection
{
    /// <summary>Let the engine pick. The default, and in V1 identical to <see cref="Docker" />.</summary>
    Auto = 0,

    /// <summary>Use Docker, and fail rather than fall back if it is not usable.</summary>
    Docker = 1
}

/// <summary>The single place the stored, wire and engine representations of <see cref="ContainerRuntimeSelection" /> meet.</summary>
/// <remarks>
///     Stored in the node settings file and carried on the API as a STRING, because the settings store serializes
///     with web defaults and no enum converter: an enum would persist as <c>0</c>/<c>1</c> in a file an operator
///     hand-edits, and would change meaning silently if a value were ever inserted. Every conversion in both
///     directions goes through these two methods, so a caller cannot hand-roll a third spelling of the mapping.
/// </remarks>
public static class ContainerRuntimeSelectionParser
{
    /// <summary>The stored and wire spelling of <see cref="ContainerRuntimeSelection.Auto" />, and the default.</summary>
    public const string Auto = "auto";

    /// <summary>The stored and wire spelling of <see cref="ContainerRuntimeSelection.Docker" />.</summary>
    public const string Docker = "docker";

    /// <summary>Parse a stored or incoming value, ordinal-ignore-case.</summary>
    /// <remarks>
    ///     <see langword="false" /> for null, blank and anything unrecognised, and <paramref name="selection" /> is
    ///     still set to <see cref="ContainerRuntimeSelection.Auto" />: a caller that ignores the result gets the
    ///     default rather than an uninitialised value, and one that reads it can reject the input.
    /// </remarks>
    public static bool TryParse(string? value, out ContainerRuntimeSelection selection)
    {
        selection = ContainerRuntimeSelection.Auto;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        if (trimmed.Equals(Auto, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.Equals(Docker, StringComparison.OrdinalIgnoreCase))
        {
            selection = ContainerRuntimeSelection.Docker;
            return true;
        }

        return false;
    }

    /// <summary>The stored and wire spelling of <paramref name="selection" />.</summary>
    public static string Format(ContainerRuntimeSelection selection)
    {
        return selection switch
        {
            ContainerRuntimeSelection.Docker => Docker,
            _ => Auto
        };
    }
}
