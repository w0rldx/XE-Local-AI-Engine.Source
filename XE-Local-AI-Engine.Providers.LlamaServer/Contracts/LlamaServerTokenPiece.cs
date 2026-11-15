namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>One token of a <c>/tokenize</c> answer requested with pieces.</summary>
public sealed class LlamaServerTokenPiece
{
    public required int Id { get; init; }

    /// <summary>The token's text with control tokens spelled out; raw bytes read as <c>&lt;0xNN&gt;</c>.</summary>
    public required string Piece { get; init; }

    /// <summary>The piece was raw bytes, not text of its own.</summary>
    public required bool IsBytes { get; init; }
}
