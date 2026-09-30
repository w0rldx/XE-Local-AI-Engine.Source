namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

/// <summary>One uploaded file's extracted text, ready to inline into a plain-chat turn.</summary>
internal readonly record struct AttachmentTextPart(string FileName, string Markdown);
