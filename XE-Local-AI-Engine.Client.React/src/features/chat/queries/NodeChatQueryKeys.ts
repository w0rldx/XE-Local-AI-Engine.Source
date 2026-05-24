export const nodeChatQueryKeys = {
	all: ["node-chat"] as const,
	conversations: () => [...nodeChatQueryKeys.all, "conversations"] as const,
	conversation: (conversationId: string) => [...nodeChatQueryKeys.conversations(), conversationId] as const,
};
