export const nodeChatQueryKeys = {
	all: ["node-chat"] as const,
	conversations: () => [...nodeChatQueryKeys.all, "conversations"] as const,
	conversationList: (includeArchived: boolean) => [...nodeChatQueryKeys.conversations(), "list", includeArchived] as const,
	conversation: (conversationId: string) => [...nodeChatQueryKeys.conversations(), conversationId] as const,
	// Per-conversation uploaded-file (attachment) list. Keyed by conversation id so switching conversations
	// loads that thread's own attachments and a brand-new conversation starts empty.
	conversationFiles: (conversationId: string) => [...nodeChatQueryKeys.conversation(conversationId), "files"] as const,
};
