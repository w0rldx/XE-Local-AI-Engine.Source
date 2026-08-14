namespace XE_Local_AI_Engine.Client.Services.Invocation;

public interface IRuntimePackageValidator
{
    /// <summary>
    ///     Validates one assembled runtime package.
    ///     <para>
    ///         <paramref name="enforceMessageSizeCap" /> is <c>true</c> only at an INBOUND seam — today the encrypted
    ///         envelope assembler, where every message in the package is untrusted platform input that has just been
    ///         decrypted. The per-turn re-validation inside the runner passes <c>false</c>: by then the context is the
    ///         node's own stored history plus node-authored synthetic context, the just-sent message was already capped
    ///         at the hub before it was persisted, and oversized history is the context budgeter's job to trim. Failing
    ///         the whole turn on a stored message is what permanently poisoned conversations — every later turn
    ///         re-validated the same row and failed again, so the user had to abandon the conversation.
    ///     </para>
    /// </summary>
    RuntimePackageValidationResult Validate(Client.Models.RuntimePackage package,
        bool enforceMessageSizeCap = true);
}
