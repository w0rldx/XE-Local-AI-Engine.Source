namespace XE_Local_AI_Engine.Client.Services.Connection;

public class WorkerNotPairedException : InvalidOperationException
{
    public WorkerNotPairedException()
        : base("Worker is not paired with the Central Platform.")
    {
    }

    public WorkerNotPairedException(string message)
        : base(message)
    {
    }
}
