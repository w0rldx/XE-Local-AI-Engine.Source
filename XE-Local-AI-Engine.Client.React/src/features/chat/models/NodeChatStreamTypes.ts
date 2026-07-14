// SignalR-stream-only DTOs for the node chat streaming path. These have NO OpenAPI/generated equivalent
// (the stream rides the SignalR hub, not the REST surface), so they live here as hand types that survive
// the hey-api REST migration. Moved verbatim out of the now-deleted NodeChatApi.ts.

export interface NodeChatStreamRequestDto {
	conversationId: string;
	content: string;
	userMessageId?: string;
	messageId?: string;
	requestId?: string;
	model?: string;
	useLocalTools?: boolean;
	// Reasoning budget for the turn ("none" | "low" | "medium" | "high"); null/absent lets the model default.
	reasoningEffort?: string;
	// Selected-path map {variantGroupId -> selectedMessageId} for the just-clicked conversation tree path. The
	// server persists it and assembles context from the selected branch only; absent falls back to the stored map.
	selectedPath?: Record<string, string>;
	// Agent to resolve for this turn. Absent/null → Default Assistant (today's built-in chat path). This is a
	// HAND-TYPED SSE DTO field (not generated), so it is safe to add here without touching NodeChatMapper.ts.
	agentDefinitionId?: string;
	// File attachments scoped to this conversation. The client re-sends ALL current (non-deleted) attachment
	// file ids on EVERY turn so the server can inline extracted text for plain chat (capped) and stage the
	// files into AgentHome for agent mode. Absent/empty → no attachments. Hand-typed SSE DTO field (not generated).
	attachmentFileIds?: string[];
	// Developer-mode per-send sampling overrides. Omitted entirely when developer mode is off or all fields
	// are null — keeps the wire payload byte-identical to the default (non-dev) path.
	samplingOptions?: {
		temperature?: number;
		topP?: number;
		topK?: number;
		minP?: number;
		maxOutputTokens?: number;
		repeatPenalty?: number;
		repeatLastN?: number;
		presencePenalty?: number;
		frequencyPenalty?: number;
		seed?: number;
		stop?: string[];
		numCtx?: number;
	};
}

export interface NodeChatStreamEventDto {
	type: string;
	conversationId: string;
	messageId: string;
	requestId: string;
	status: string;
	sequence: number;
	occurredAtUtc: number;
	delta?: string | null;
	reasoningDelta?: string | null;
	content?: string | null;
	reasoning?: string | null;
	error?: string | null;
	model?: string | null;
	inputTokens?: number | null;
	outputTokens?: number | null;
	totalTokens?: number | null;
	reasoningTokens?: number | null;
	// Tool lifecycle fields: present on `tool-call-requested` / `tool-call-completed` events only.
	toolCallId?: string | null;
	toolName?: string | null;
	arguments?: string | null;
	requiresApproval?: boolean | null;
	result?: string | null;
	isError?: boolean | null;
	// Notice fields: present on the `assistant-notice` event only. `noticeKind` is one of "ModelSubstituted" |
	// "ToolDisabled" | "HistoryTruncated" | "AttachmentsWithheld"; unknown kinds render via the generic fallback.
	// `noticeMessage` is the sanitized, user-facing sentence to display verbatim.
	noticeKind?: string | null;
	noticeMessage?: string | null;
}

export const nodeChatToolStreamEventTypes = {
	toolCallRequested: "tool-call-requested",
	toolCallCompleted: "tool-call-completed",
} as const;
