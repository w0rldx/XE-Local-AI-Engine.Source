namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

/// <summary>One persisted marker as the store hands it back: its identity (used to delete it) and its contents.</summary>
public sealed record SandboxMarkerEntry(string MarkerId, SandboxProcessMarker Marker);
