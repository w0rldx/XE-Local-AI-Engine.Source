export const nodeChatQueryKeys = {
	all: ["node-chat"] as const,
	conversations: () => [...nodeChatQueryKeys.all, "conversations"] as const,
	// Prefix covering BOTH conversation-list variants (includeArchived true/false) and nothing else. Use this to
	// refresh the sidebar list without the `conversations()` prefix's side effect of also invalidating every cached
	// conversation detail AND each detail's `files` child (the per-turn refresh once did that: the detail refetched
	// twice per turn — once directly, once via the broad prefix landing mid-flight — and uploads refetched for free).
	conversationLists: () => [...nodeChatQueryKeys.conversations(), "list"] as const,
	conversationList: (includeArchived: boolean) => [...nodeChatQueryKeys.conversationLists(), includeArchived] as const,
	conversation: (conversationId: string) => [...nodeChatQueryKeys.conversations(), conversationId] as const,
	// Per-conversation uploaded-file (attachment) list. Keyed by conversation id so switching conversations
	// loads that thread's own attachments and a brand-new conversation starts empty.
	conversationFiles: (conversationId: string) => [...nodeChatQueryKeys.conversation(conversationId), "files"] as const,
};
