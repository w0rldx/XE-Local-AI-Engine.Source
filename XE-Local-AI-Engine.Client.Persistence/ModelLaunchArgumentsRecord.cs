namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Worker-side projection of a persisted <c>ModelLaunchArguments</c> row: the model name and the raw operator-entered
///     extra <c>llama-server</c> argument string. Read on the cold spawn path to append the operator's experimentation
///     flags to the launched process, and by the settings endpoint to render the current override.
/// </summary>
public sealed record ModelLaunchArgumentsRecord(string ModelName, string RawArguments, long UpdatedAtUtc);
