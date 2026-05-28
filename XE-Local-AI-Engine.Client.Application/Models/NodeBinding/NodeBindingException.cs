namespace XE_Local_AI_Engine.Client.Models.NodeBinding;

public class NodeBindingException : Exception
{
    public NodeBindingException(string message) : base(message)
    {
    }

    public NodeBindingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
