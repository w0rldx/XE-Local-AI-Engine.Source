namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    public static class LocalChat
    {
        public const string Hub = "/api/local/v1/chat/hub";
        public const string Conversations = "chat/conversations";
        public const string ConversationById = "chat/conversations/{conversationId}";
        public const string RenameConversation = "chat/conversations/{conversationId}/rename";
        public const string PinConversation = "chat/conversations/{conversationId}/pin";
        public const string ArchiveConversation = "chat/conversations/{conversationId}/archive";

        // Per-conversation temporary-chat (memory-excluded) override (adaptive memory). Literal "memory-excluded"
        // segment keeps it distinct from the other conversation action routes.
        public const string MemoryExcludedConversation = "chat/conversations/{conversationId}/memory-excluded";

        // Non-destructive compaction: summarize the older turns into a synopsis sent in their place. POST (an action that
        // mutates derived state), distinct literal "compact" segment.
        public const string CompactConversation = "chat/conversations/{conversationId}/compact";

        // Read-only view of the distilled state and the synopsis the compaction path maintains. GET, literal "context-state" segment.
        public const string ConversationContextState = "chat/conversations/{conversationId}/context-state";

        // Pre-send estimate of the next request's fixed parts (prompt, offered tools, reserve, margin) for a model and agent. GET, query-only.
        public const string ContextEstimate = "chat/context-estimate";
        public const string BranchConversation = "chat/conversations/{conversationId}/branch/{messageId}";
        public const string MessageRevisions = "chat/conversations/{conversationId}/messages/{messageId}/revisions";
        public const string MessageFeedback = "chat/conversations/{conversationId}/messages/{messageId}/feedback";
        public const string SelectedPath = "chat/conversations/{conversationId}/selected-path";

        // Per-conversation uploaded-file attachments: the collection (POST multipart upload, GET list) and one file resource (DELETE).
        // The literal "uploads" segment keeps these off the other conversation action routes; {fileId} is server-generated, never a client-supplied path.
        public const string ConversationUploads = "chat/conversations/{conversationId}/uploads";
        public const string ConversationUploadById = "chat/conversations/{conversationId}/uploads/{fileId}";
        public const string Cancel = "chat/cancel";

        // "Answer now": ends the running turn's reasoning block so the model answers, without stopping the turn. Keyed by the assistant
        // message the stream is writing; a sibling of Cancel, which stops the turn outright.
        public const string AnswerNow = "chat/messages/{messageId}/answer-now";

        // Loopback tool-approval responder: desktop/local mode has no worker hub for an MCP tool's approval round-trip, so the browser posts the operator's decision
        // here and the in-process invocation runner releases the waiting turn. Literal segments keep it off the chat action routes; the body carries the request id + decision, no route param.
        public const string ResolveApproval = "chat/approvals/resolve";

        // Loopback ask_user responder, the question analogue of ResolveApproval: the browser posts the operator's answers here and the in-process runner releases
        // the parked turn. Literal segments keep it off the chat action routes; the body carries the question request id + the answers, no route param.
        public const string ResolveUserQuestion = "chat/questions/resolve";
    }
}
