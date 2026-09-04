// SignalR-stream-only DTOs for the node chat streaming path. These have NO OpenAPI/generated equivalent
// (the stream rides the SignalR hub, not the REST surface), so they remain hand-authored beside the chat feature.

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
	// HAND-TYPED SignalR stream DTO field (not generated), so it is safe to add here without touching NodeChatMapper.ts.
	agentDefinitionId?: string;
	// File attachments scoped to this conversation. The client re-sends ALL current (non-deleted) attachment
	// file ids on EVERY turn so the server can inline extracted text for plain chat (capped) and stage the
	// files into AgentHome for agent mode. Absent/empty → no attachments.
	// Hand-typed SignalR stream DTO field (not generated).
	attachmentFileIds?: string[];
	// Opt-in knowledge-base grounding for a plain-chat turn (default off). When true the server retrieves
	// the top-k knowledge-base hits for this message and inlines them (fenced, capped) into the turn, surfacing
	// their sources on the assistant turn. Ignored in agent mode.
	// Hand-typed SignalR stream DTO field (not generated).
	useKnowledgeBase?: boolean;
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
		// Seed rides the wire as a precision-safe string (mirrors the backend SamplingOptions.Seed contract).
		seed?: string;
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
	// Character index (UTF-16 code units, the same space as JS `String.length`) in the accumulated content /
	// reasoning at which this event's `delta` / `reasoningDelta` begins. Set on `assistant-delta` (where it is
	// the gap/overlap check) and on `assistant-snapshot` (where it equals the carried text's length); absent
	// everywhere else. `NodeChatAdapter` is the only reader — it repairs a mismatch via ResumeMessage.
	contentOffset?: number | null;
	reasoningOffset?: number | null;
	// `content`/`reasoning` carry the FULL accumulated text and are populated only on `assistant-snapshot`,
	// the terminals, and `user-message-persisted`. They are NEVER populated on `assistant-delta` — reading
	// them there is what made every frame carry the whole message (the O(n^2) wire amplifier).
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
	// `noticeMessage` is the sanitized, user-facing sentence to display verbatim. `noticeDetail` is the notice's
	// optional structured detail beside that prose — a stable machine code or short identifier naming WHY it fired
	// (the kebab-case dispatch reason for "EffortDispatched", the effective model for the withheld kinds). Rendered
	// as-is, never translated, and absent on notices that carry none.
	noticeKind?: string | null;
	noticeMessage?: string | null;
	noticeDetail?: string | null;
	// Runtime phase: present on the `assistant-phase` event only. One of "preparing_runtime" |
	// "loading_model" | "generating" — emitted before the first token while a local model cold-loads, so the UI can
	// show a distinct "Loading model…" indicator instead of the generic typing dots. Absent for cloud/Ollama turns.
	runtimePhase?: string | null;
	// Approval request id: present on the `approval-requested` event only. The durable key the browser echoes
	// back to the loopback resolve endpoint to release the waiting tool call. `toolCallId` carries the tool-call id the
	// approval belongs to (so the Approve/Deny controls attach to the matching card) and `toolName` the tool name.
	approvalRequestId?: string | null;
	// Question request id: present on the `question-requested` event only. The durable key the browser echoes back to
	// the loopback resolve endpoint to release the turn parked on an `ask_user` call. `toolCallId` carries the tool-call
	// id the question belongs to (so the card attaches to the right slot) and `toolName` the tool name — same as the
	// approval event. `questions` is the raw question JSON the model emitted; parsed by `parsePendingUserQuestion`,
	// the ONE place this wire shape is read.
	questionRequestId?: string | null;
	questions?: string | null;
	// Whether the node can actually REMEMBER an "approve for this session" decision for THIS request. Present on the
	// `approval-requested` event only, and absent when the backend could not resolve it (the reconnect replay). The
	// runner answers from the same memo-key resolution that would honour the decision, so it sees the per-call
	// narrowings the tool catalog cannot — `ToolCallCard` prefers it and falls back to the catalog flag when absent.
	sessionScopeEligible?: boolean | null;
	// The effective whole-turn ceiling for THIS turn in seconds — the operator's node "Maximum message request
	// timeout" as the backend resolved it into the run's TimeoutSettings.InvocationTimeoutSeconds. Present on
	// `assistant-queued` and `assistant-streaming` only. `NodeChatStreamGuard` is the only reader: it derives its own
	// watchdog deadlines from it so the browser never pre-empts the node's own ceiling.
	invocationTimeoutSeconds?: number | null;
}

export const nodeChatToolStreamEventTypes = {
	toolCallRequested: "tool-call-requested",
	toolCallCompleted: "tool-call-completed",
	// A pending tool-approval request: flips the matching tool card into a waiting-for-approval state carrying
	// the approvalRequestId the Approve/Deny controls post back. Distinct from a plain tool-call-requested.
	approvalRequested: "approval-requested",
	// A pending `ask_user` question: flips the matching tool card into a waiting state carrying the question payload
	// the inline answer card renders and posts back. Structurally identical to approval-requested, richer payload.
	questionRequested: "question-requested",
} as const;
