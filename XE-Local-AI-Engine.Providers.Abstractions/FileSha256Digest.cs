namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Runtime.InteropServices;

/// <summary>A file's lowercase hex SHA-256 and its length in bytes, both read from the same open stream.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct FileSha256Digest(string Sha256, long SizeBytes);
