namespace XE_Local_AI_Engine.Client.Services.Invocation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The sandbox command each chat tool call is running right now, keyed by the assistant message and the tool call id,
///     so the operator can stop that one command and nothing else.
/// </summary>
/// <remarks>
///     Stopping goes through <see cref="ISandboxRuntimeProvider.CancelCommandAsync" />, which ends the command with a
///     non-throwing <c>Completed = false</c> result, and marks the registration so the tool can say it was stopped rather
///     than timed out. A singleton because the stop arrives on a request call stack, like <c>InvocationReasoningControl</c>.
/// </remarks>
public sealed class ToolCommandStopRegistry
{
    private readonly ConcurrentDictionary<string, ToolCommandStopRegistration> _running = new(StringComparer.Ordinal);

    /// <summary>Registers the running command until the registration is disposed; null when the provider cannot cancel one command.</summary>
    public ToolCommandStopRegistration? Register(Guid messageId, string toolCallId, ISandboxRuntimeProvider provider, SandboxHandle handle, string executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrEmpty(executionId);

        if (!provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsCommandCancellation))
        {
            return null;
        }

        var registration = new ToolCommandStopRegistration(KeyOf(messageId, toolCallId), provider, handle, executionId, Remove);
        _running[registration.Key] = registration;
        return registration;
    }

    /// <summary>Asks the provider to stop the command running for that call; false when none is running.</summary>
    public async Task<bool> TryStopAsync(Guid messageId, string toolCallId, CancellationToken cancellationToken)
    {
        if (!_running.TryGetValue(KeyOf(messageId, toolCallId), out var registration))
        {
            return false;
        }

        // Marked before the cancel, which can return the tool its result before this method resumes.
        registration.StopRequested = true;
        try
        {
            await registration.Provider.CancelCommandAsync(registration.Handle, registration.ExecutionId, cancellationToken);
            return true;
        }
        catch (SandboxHandleInvalidException)
        {
            // The command finished and its sandbox went away between the lookup and the cancel.
            registration.StopRequested = false;
            return false;
        }
    }

    private static string KeyOf(Guid messageId, string toolCallId) =>
        messageId.ToString("N") + "/" + toolCallId;

    private void Remove(ToolCommandStopRegistration registration)
    {
        _ = _running.TryRemove(new KeyValuePair<string, ToolCommandStopRegistration>(registration.Key, registration));
    }
}
