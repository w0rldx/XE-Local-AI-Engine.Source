namespace XE_Local_AI_Engine.Client.Hosting;

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

/// <summary>
///     Recognizes an assembly load that Windows application control refused (Smart App Control or a WDAC policy), so
///     startup can name the blocked file instead of crashing with a generic failure.
/// </summary>
/// <remarks>
///     The desktop shell reads the stderr line <see cref="Describe" /> builds: it starts with
///     <see cref="StandardErrorPrefix" /> and goes with exit code 11. Keep both stable.
/// </remarks>
internal static class ApplicationControlBlock
{
    /// <summary>The stderr prefix the desktop shell matches; the blocked file name follows it.</summary>
    internal const string StandardErrorPrefix = "Application control blocked: ";

    /// <summary>HRESULT of <c>ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION</c>: "An Application Control policy has blocked this file."</summary>
    private const int BlockedByPolicyHResult = unchecked((int)0x800711C7);

    /// <summary>
    ///     Finds a load refused by application control in <paramref name="exception" />, its inner exceptions, or a
    ///     <see cref="ReflectionTypeLoadException" />'s loader exceptions.
    /// </summary>
    /// <param name="exception">The startup failure.</param>
    /// <param name="fileName">The blocked file's name (no directory), when found.</param>
    internal static bool TryDescribe(Exception? exception, [NotNullWhen(true)] out string? fileName)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var blockedFile = current switch
            {
                FileLoadException { HResult: BlockedByPolicyHResult } load => load.FileName ?? string.Empty,
                BadImageFormatException { HResult: BlockedByPolicyHResult } image => image.FileName ?? string.Empty,
                _ => null
            };
            if (blockedFile is not null)
            {
                fileName = ToFileName(blockedFile);
                return true;
            }

            if (current is ReflectionTypeLoadException typeLoad)
            {
                foreach (var loaderException in typeLoad.LoaderExceptions)
                {
                    if (TryDescribe(loaderException, out fileName))
                    {
                        return true;
                    }
                }
            }
        }

        fileName = null;
        return false;
    }

    /// <summary>The single stderr line for a blocked file, starting with <see cref="StandardErrorPrefix" />.</summary>
    internal static string Describe(string fileName) =>
        $"{StandardErrorPrefix}{fileName}. Windows Smart App Control or an application control policy blocked this file in the "
        + "install folder, so the engine cannot start.";

    // FileName is the assembly's display name or its full path, depending on how the loader found it. Both separators
    // are split, so a Windows path reads the same wherever this runs.
    private static string ToFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "an engine file";
        }

        return fileName[(fileName.LastIndexOfAny(['\\', '/']) + 1)..];
    }
}
