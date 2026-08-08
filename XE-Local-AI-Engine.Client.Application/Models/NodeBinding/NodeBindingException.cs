namespace XE_Local_AI_Engine.Client.Models.NodeBinding;

/// <summary>
///     Exception raised for node binding failures.
/// </summary>
public class NodeBindingException : Exception
{
    public NodeBindingException(string message) : base(message)
    {
    }

    public NodeBindingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
