namespace XE_Local_AI_Engine.Client.Services.Invocation;

public interface IRuntimePackageValidator
{
    /// <summary>Validates one assembled runtime package.</summary>
    /// <remarks>
    ///     <paramref name="enforceMessageSizeCap" /> is <c>true</c> only at an INBOUND seam, where every message is
    ///     untrusted input that has just been decrypted. The per-turn re-validation inside the runner passes
    ///     <c>false</c>: by then the context is the node's own stored history plus node-authored synthetic context, the
    ///     just-sent message was already capped at the hub before it was persisted, and oversized history is the
    ///     budgeter's job. Failing a turn on a stored message wedges every later turn of that conversation.
    /// </remarks>
    RuntimePackageValidationResult Validate(Client.Models.RuntimePackage package,
        bool enforceMessageSizeCap = true);
}
