namespace XE_Local_AI_Engine.Client.Hosting;

internal sealed record ReadyInfo(string Version, string Url, string McpUrl, string DataDir, int Pid, DateTimeOffset StartedAtUtc);
