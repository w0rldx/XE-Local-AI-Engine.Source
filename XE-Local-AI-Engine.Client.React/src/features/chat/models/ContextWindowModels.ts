// Its own module so both the chat models and the hand-written stream DTOs can import it without a cycle.
// Content-free account of how one model request occupied its context window (mirrors the backend
// `NodeChatContextWindowDto`, camelCase). Names and counts only. `kind` tells a last provider round apart from a
// pre-send estimate; provider* fields are null pre-send, `estimated` is null when no estimate was taken.
export type ContextWindowKind = "LastRound" | "PreSendEstimate";

export interface ContextWindowEstimate {
	systemPromptTokens: number;
	instructionsTokens: number;
	toolSchemaTokens: number;
	toolTemplatePreambleTokens: number;
	knowledgeTokens: number;
	attachmentTokens: number;
	compactionTokens: number;
	conversationTokens: number;
	totalTokens: number;
}

export interface ContextWindowTool {
	name: string;
	tokens: number;
}

export interface ContextWindowTrim {
	messagesDropped: number;
	toolResultsTruncated: number;
	reasoningStripped: number;
}

export interface ContextWindowSnapshot {
	kind: ContextWindowKind;
	modelId?: string | null;
	windowTokens: number;
	reservedOutputTokens: number;
	usableWindowTokens: number;
	safetyMarginTokens: number;
	providerInputTokens?: number | null;
	providerOutputTokens?: number | null;
	providerReasoningTokens?: number | null;
	estimated?: ContextWindowEstimate | null;
	tools: ContextWindowTool[];
	toolsWithheldCount: number;
	trimmed?: ContextWindowTrim | null;
}
