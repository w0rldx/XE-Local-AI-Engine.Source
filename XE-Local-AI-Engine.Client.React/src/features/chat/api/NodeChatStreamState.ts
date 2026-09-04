import { parsePendingUserQuestion } from "@/features/chat/api/AskUserQuestionWire";
import { mapToolCallEvent } from "@/features/chat/api/NodeChatMapper";
import type {
	ChatConversationModel,
	ChatMessageModel,
	ChatMessagePart,
	ChatStreamingState,
	ChatTimelineEntry,
	ChatToolCall,
	MessageStatus,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";
import {
	buildMessageParts,
	type NoticeEntryInput,
	type ReasoningSegmentInput,
	type TextSegmentInput,
	type ToolEntryInput,
} from "@/features/chat/models/MessageParts";
import { type NodeChatStreamEventDto, nodeChatToolStreamEventTypes } from "@/features/chat/models/NodeChatStreamTypes";

export const nodeChatStreamEventTypes = {
	userMessagePersisted: "user-message-persisted",
	assistantPending: "assistant-pending",
	assistantQueued: "assistant-queued",
	assistantStreaming: "assistant-streaming",
	// A pre-first-token runtime-phase transition. Never mutates content/status — it only carries the
	// runtime phase forward so the UI can show a "Loading model…" indicator during a local cold load.
	assistantPhase: "assistant-phase",
	assistantDelta: "assistant-delta",
	// A mid-stream replacement of the accumulated text (resume replay, offset-gap repair, queue-overflow
	// repair). Carries the full `content`/`reasoning` and no delta. It is NOT terminal — its wire status is
	// `streaming` and the turn stays live — so it must never appear in `terminalStatusForEvent`.
	assistantSnapshot: "assistant-snapshot",
	assistantCompleted: "assistant-completed",
	assistantCancelled: "assistant-cancelled",
	assistantFailed: "assistant-failed",
	assistantInterrupted: "assistant-interrupted",
	// Tool lifecycle events reuse the dedicated tool-event constant so the wire names stay DRY.
	toolCallRequested: nodeChatToolStreamEventTypes.toolCallRequested,
	toolCallCompleted: nodeChatToolStreamEventTypes.toolCallCompleted,
	// A pending tool-approval request: flips the matching tool card into a waiting-for-approval state without
	// mutating content/status — the turn stays live while the operator decides.
	approvalRequested: nodeChatToolStreamEventTypes.approvalRequested,
	// A pending `ask_user` question: flips the matching tool card into a waiting state carrying the question payload
	// the inline answer card renders. Same contract as approvalRequested — the turn stays live while the user answers.
	questionRequested: nodeChatToolStreamEventTypes.questionRequested,
	// A non-fatal "turn notice" (model substitution, tool disabled, history truncated). Never mutates
	// content/status — it only appends a notice part to the current assistant turn and keeps the turn live.
	assistantNotice: "assistant-notice",
} as const;

function isToolStreamEvent(eventType: string): boolean {
	return eventType === nodeChatStreamEventTypes.toolCallRequested || eventType === nodeChatStreamEventTypes.toolCallCompleted;
}

const knownStatuses = new Set<MessageStatus>([
	"pending",
	"queued",
	"streaming",
	"completed",
	"cancelled",
	"failed",
	"interrupted",
]);

export interface OptimisticNodeChatSendIds {
	userMessageId: string;
	assistantMessageId: string;
	requestId: string;
}

export interface AppliedNodeChatStreamEvent {
	conversation: ChatConversationModel;
	streamingMessage: ChatStreamingState;
	isTerminal: boolean;
	// A tool-lifecycle event yields a timeline entry to accumulate (keyed by tool call id) instead of mutating
	// assistant content; assistant/lifecycle events leave this undefined.
	timelineEntry?: ChatTimelineEntry;
}

/**
 * Wraps the `ChatToolCall` mapped from a tool-lifecycle event into the `ChatTimelineEntry` the render pipeline
 * (`ChatMessage`'s `calls()` helper) consumes — scoped to the streaming assistant message so `ChatMessageList`
 * filters it onto the right turn. The tool call id is the entry id so a `tool-call-completed` collapses onto its
 * matching `tool-call-requested` entry instead of duplicating it.
 */
function toToolTimelineEntry(event: NodeChatStreamEventDto): ChatTimelineEntry | undefined {
	const toolCall = mapToolCallEvent(event);
	if (!toolCall) {
		return undefined;
	}

	return {
		id: toolCall.id,
		messageId: event.messageId,
		invocationId: event.requestId || undefined,
		type: event.type === nodeChatStreamEventTypes.toolCallCompleted ? "ToolResult" : "ToolCall",
		toolName: toolCall.name,
		toolArgs: toolCall.args,
		toolResult: toolCall.result,
		state: toolCall.state,
		requiresApproval: toolCall.requiresApproval,
		createdAt: isoFromUnixMilliseconds(event.occurredAtUtc),
	};
}

/**
 * Accumulates a tool timeline entry per streaming turn: a `tool-call-completed` updates the matching
 * `tool-call-requested` entry (same tool call id) in place rather than appending a duplicate.
 */
export function accumulateToolTimelineEntry(entries: ChatTimelineEntry[], entry: ChatTimelineEntry): ChatTimelineEntry[] {
	const existingIndex = entries.findIndex((candidate) => candidate.id === entry.id);
	if (existingIndex < 0) {
		return [...entries, entry];
	}

	return entries.map((candidate, index) =>
		index === existingIndex
			? {
					...candidate,
					...entry,
					// The completed tool-event omits the approval requirement (it lives only on the requested
					// event), so preserve it from whichever entry defines it instead of letting the spread
					// clobber the flag to undefined.
					requiresApproval: entry.requiresApproval ?? candidate.requiresApproval,
				}
			: candidate,
	);
}

/**
 * Splits the prior ordered `parts[]` back into reasoning segments, tool entries, text segments, and notice entries
 * so the accumulator can extend them without losing any existing interleave. All four kinds must round-trip so the
 * "byte-identical live vs reload" invariant holds for resumed/re-attached turns that carry text/notice parts.
 */
function decomposeParts(parts: readonly ChatMessagePart[] | undefined): {
	reasoningSegments: ReasoningSegmentInput[];
	toolEntries: ToolEntryInput[];
	textSegments: TextSegmentInput[];
	noticeEntries: NoticeEntryInput[];
} {
	const reasoningSegments: ReasoningSegmentInput[] = [];
	const toolEntries: ToolEntryInput[] = [];
	const textSegments: TextSegmentInput[] = [];
	const noticeEntries: NoticeEntryInput[] = [];
	for (const part of parts ?? []) {
		if (part.kind === "reasoning") {
			reasoningSegments.push({ id: part.id, sequence: part.sequence, text: part.text });
		} else if (part.kind === "tool") {
			toolEntries.push({
				id: part.id,
				sequence: part.sequence,
				name: part.name,
				state: part.state,
				args: part.args,
				result: part.result,
				requiresApproval: part.requiresApproval,
				pendingApprovalRequestId: part.pendingApprovalRequestId,
				pendingApprovalSessionScopeEligible: part.pendingApprovalSessionScopeEligible,
				pendingQuestion: part.pendingQuestion,
			});
		} else if (part.kind === "text") {
			textSegments.push({ id: part.id, sequence: part.sequence, text: part.text });
		} else if (part.kind === "notice") {
			noticeEntries.push({ id: part.id, sequence: part.sequence, noticeKind: part.noticeKind, text: part.text, detail: part.detail });
		}
	}

	return { reasoningSegments, toolEntries, textSegments, noticeEntries };
}

/** The highest `sequence` across all accumulated parts — i.e. the wire position of the most recent part. */
function maxPartSequence(
	reasoningSegments: readonly ReasoningSegmentInput[],
	toolEntries: readonly ToolEntryInput[],
	textSegments: readonly TextSegmentInput[] = [],
	noticeEntries: readonly NoticeEntryInput[] = [],
): number {
	let max = Number.NEGATIVE_INFINITY;
	for (const segment of reasoningSegments) {
		max = Math.max(max, segment.sequence);
	}
	for (const entry of toolEntries) {
		max = Math.max(max, entry.sequence);
	}
	for (const segment of textSegments) {
		max = Math.max(max, segment.sequence);
	}
	for (const entry of noticeEntries) {
		max = Math.max(max, entry.sequence);
	}

	return max;
}

/**
 * Folds a reasoning delta into the ordered segments: when the most recent part is the trailing reasoning run the
 * delta extends it; when the most recent part is a tool, text, or notice (or there is no segment yet) a NEW
 * reasoning segment opens at this event's `sequence` — this is what produces the second Thoughts block after a
 * tool (Option A interleave).
 */
function appendReasoningDelta(
	reasoningSegments: ReasoningSegmentInput[],
	toolEntries: readonly ToolEntryInput[],
	textSegments: readonly TextSegmentInput[],
	noticeEntries: readonly NoticeEntryInput[],
	messageId: string,
	sequence: number,
	delta: string,
): ReasoningSegmentInput[] {
	const trailing = reasoningSegments.at(-1);
	const lastIsTrailingReasoning =
		trailing !== undefined && trailing.sequence >= maxPartSequence(reasoningSegments, toolEntries, textSegments, noticeEntries);

	if (lastIsTrailingReasoning && trailing) {
		return reasoningSegments.map((segment, index) =>
			index === reasoningSegments.length - 1 ? { ...segment, text: `${segment.text}${delta}` } : segment,
		);
	}

	return [...reasoningSegments, { id: `${messageId}:${sequence}`, sequence, text: delta }];
}

/** Merges a tool call (collapsed requested→completed by id) into the ordered tool entries, preserving its slot. */
function mergeToolEntry(toolEntries: readonly ToolEntryInput[], toolCall: ChatToolCall, sequence: number): ToolEntryInput[] {
	// A prompt replay that cannot be correlated to a persisted call is represented by a generic request-id-keyed card.
	// Once a terminal tool event arrives, replace that fallback with the real call/result rather than leaving an empty
	// generic card beside it. Request-id-keyed entries are distinguishable from correlated cards because their id is
	// exactly their pending approval/question request id.
	const entriesWithoutResolvedGenericPrompt =
		toolCall.state === "received" || toolCall.state === "failed"
			? toolEntries.filter((entry) => {
					const promptRequestId = entry.pendingApprovalRequestId ?? entry.pendingQuestion?.requestId;
					const isGenericPrompt = promptRequestId !== undefined && entry.id === promptRequestId;
					return !isGenericPrompt || (entry.name !== "tool" && entry.name !== toolCall.name);
				})
			: toolEntries;
	const existingIndex = entriesWithoutResolvedGenericPrompt.findIndex((entry) => entry.id === toolCall.id);
	if (existingIndex < 0) {
		return [
			...entriesWithoutResolvedGenericPrompt,
			{
				id: toolCall.id,
				sequence,
				name: toolCall.name,
				state: toolCall.state,
				args: toolCall.args,
				result: toolCall.result,
				requiresApproval: toolCall.requiresApproval,
			},
		];
	}

	return entriesWithoutResolvedGenericPrompt.map((entry, index) =>
		index === existingIndex
			? {
					...entry,
					// Keep the original slot's sequence so the completed event never reorders the card; merge the
					// latest state/args/result and preserve the requested event's approval flag (the completed event
					// omits it, so a naive spread would clobber it to undefined).
					name: toolCall.name || entry.name,
					state: toolCall.state,
					args: toolCall.args ?? entry.args,
					result: toolCall.result ?? entry.result,
					requiresApproval: toolCall.requiresApproval ?? entry.requiresApproval,
					// The tool has resolved (approved → executed, or rejected → synthetic result) once it reaches a
					// terminal state, so clear the pending-approval prompt; otherwise carry it forward (a plain
					// requested/completed event never sets it — only the approval-requested event does).
					pendingApprovalRequestId:
						toolCall.state === "received" || toolCall.state === "failed" ? undefined : entry.pendingApprovalRequestId,
					pendingApprovalSessionScopeEligible:
						toolCall.state === "received" || toolCall.state === "failed" ? undefined : entry.pendingApprovalSessionScopeEligible,
					// Same rule for an `ask_user` question: once the tool call resolves, the answer has been consumed
					// (or the wait timed out), so the inline question card must not survive on the resolved card.
					pendingQuestion: toolCall.state === "received" || toolCall.state === "failed" ? undefined : entry.pendingQuestion,
				}
			: entry,
	);
}

/**
 * Folds a pending human prompt (a tool approval, or an `ask_user` question) into the ordered tool entries: the
 * matching tool card (by tool-call id) flips to `waiting` and carries the prompt payload the inline controls post
 * back. When the gated tool has not yet surfaced its own tool-call-requested card, a fresh waiting entry is created
 * so the prompt still renders. Both prompts share this path because they are the same state transition on the wire —
 * only the carried payload differs (`pendingApprovalRequestId` vs `pendingQuestion`).
 */
function mergePendingPromptIntoToolEntries(
	toolEntries: readonly ToolEntryInput[],
	callId: string | undefined,
	toolName: string | undefined,
	sequence: number,
	prompt: Pick<ToolEntryInput, "pendingApprovalRequestId" | "pendingApprovalSessionScopeEligible" | "pendingQuestion">,
): ToolEntryInput[] {
	const normalizedCallId = callId?.trim() || undefined;
	const normalizedToolName = toolName?.trim() || undefined;
	const promptRequestId = prompt.pendingApprovalRequestId?.trim() || prompt.pendingQuestion?.requestId.trim() || undefined;
	let existingIndex = normalizedCallId ? toolEntries.findIndex((entry) => entry.id === normalizedCallId) : -1;
	if (existingIndex < 0 && !normalizedCallId && promptRequestId) {
		existingIndex = toolEntries.findIndex((entry) => entry.id === promptRequestId);
	}

	// A reconnect replay can lack call metadata even though the just-refetched persisted parts already carry the
	// unresolved tool card. Reuse that card only when the target is unambiguous; inventing an event-sequence id here
	// creates a second uncategorized card that survives after the real call completes. If several live cards exist and
	// the replay cannot identify one, leave them unchanged rather than attach approval controls to the wrong tool.
	if (existingIndex < 0 && !normalizedCallId) {
		const unresolvedIndexes = toolEntries
			.map((entry, index) => ({ entry, index }))
			.filter(({ entry }) => entry.state === "requesting" || entry.state === "waiting")
			.filter(({ entry }) => !normalizedToolName || entry.name === normalizedToolName)
			.map(({ index }) => index);
		const unresolvedIndex = unresolvedIndexes[0];
		if (unresolvedIndexes.length !== 1 || unresolvedIndex === undefined) {
			return promptRequestId
				? [
						...toolEntries,
						{
							id: promptRequestId,
							sequence,
							name: normalizedToolName ?? "tool",
							state: "waiting",
							requiresApproval: true,
							...prompt,
						},
					]
				: [...toolEntries];
		}

		existingIndex = unresolvedIndex;
	}

	if (existingIndex < 0) {
		return [
			...toolEntries,
			{
				id: normalizedCallId ?? promptRequestId ?? `${normalizedToolName ?? "tool"}:${sequence}`,
				sequence,
				name: normalizedToolName ?? "tool",
				state: "waiting",
				requiresApproval: true,
				...prompt,
			},
		];
	}

	return toolEntries.map((entry, index) =>
		index === existingIndex
			? {
					...entry,
					// Preserve the original slot's sequence so the card never reorders; flip it into the waiting state
					// and attach the prompt payload + approval flag.
					name: normalizedToolName ?? entry.name,
					state: "waiting",
					requiresApproval: true,
					...prompt,
				}
			: entry,
	);
}

/**
 * Builds the next ordered `parts[]` for an assistant delta/terminal event, extending the prior parts with this
 * event's reasoning. A `reasoningDelta` folds into the trailing segment (or opens a new one after a tool); a full
 * `reasoning` replacement (terminal/resume) reseeds a single leading reasoning segment when no segments exist yet
 * so the tool interleave is preserved while reasoning catches up. Returns undefined when the turn has no parts.
 */
function nextReasoningParts(
	existing: ChatMessageModel | undefined,
	event: NodeChatStreamEventDto,
	reasoning: string | undefined,
): ChatMessagePart[] | undefined {
	const { reasoningSegments, toolEntries, textSegments, noticeEntries } = decomposeParts(existing?.parts);

	let nextReasoningSegments = reasoningSegments;
	if (event.reasoningDelta) {
		nextReasoningSegments = appendReasoningDelta(
			reasoningSegments,
			toolEntries,
			textSegments,
			noticeEntries,
			event.messageId,
			event.sequence,
			event.reasoningDelta,
		);
	} else if (reasoning && reasoningSegments.length === 0) {
		// A full reasoning value with no prior segment (e.g. terminal/resume rehydrate): seed one leading segment.
		nextReasoningSegments = [{ id: `${event.messageId}:${event.sequence}`, sequence: event.sequence, text: reasoning }];
	}

	if (nextReasoningSegments.length === 0 && toolEntries.length === 0 && textSegments.length === 0 && noticeEntries.length === 0) {
		return undefined;
	}

	return buildMessageParts(nextReasoningSegments, toolEntries, textSegments, noticeEntries);
}

/**
 * Terminalizes any lingering waiting tool card that still carries an unanswered human prompt (a tool approval or an
 * `ask_user` question) on a terminal turn. An API-tool DENY never emits a tool-call-completed event — the deny
 * short-circuits before the tool ever runs — so its requestId-keyed waiting card would otherwise linger with live
 * Approve/Deny controls until the post-stream refetch scrubs it; a question whose turn was cancelled or timed out is
 * the same shape. Once the turn is terminal the prompt can no longer be answered, so the card is flipped to `failed`
 * and its prompt cleared. Returns the same array reference when nothing changed so unaffected turns keep referential
 * stability.
 */
function clearPendingPromptWaitingCards(parts: ChatMessagePart[] | undefined): ChatMessagePart[] | undefined {
	if (!parts) {
		return parts;
	}

	let changed = false;
	const next = parts.map((part) => {
		if (
			part.kind === "tool" &&
			part.state === "waiting" &&
			(typeof part.pendingApprovalRequestId === "string" || part.pendingQuestion !== undefined)
		) {
			changed = true;
			return {
				...part,
				state: "failed" as const,
				pendingApprovalRequestId: undefined,
				pendingApprovalSessionScopeEligible: undefined,
				pendingQuestion: undefined,
			};
		}

		return part;
	});

	return changed ? next : parts;
}

function normalizeStatus(status: string | null | undefined, fallback: MessageStatus): MessageStatus {
	const normalized = status?.toLowerCase() as MessageStatus | undefined;
	return normalized && knownStatuses.has(normalized) ? normalized : fallback;
}

function terminalStatusForEvent(eventType: string): MessageStatus | undefined {
	switch (eventType) {
		case nodeChatStreamEventTypes.assistantCompleted:
			return "completed";
		case nodeChatStreamEventTypes.assistantCancelled:
			return "cancelled";
		case nodeChatStreamEventTypes.assistantFailed:
			return "failed";
		case nodeChatStreamEventTypes.assistantInterrupted:
			return "interrupted";
		default:
			return undefined;
	}
}

/**
 * How an assistant lifecycle event's text fields fold into the accumulated turn, under the delta-only wire
 * contract: `append` for a delta (which carries ONLY its delta — its `content`/`reasoning` are never populated,
 * and reading them is what made every frame re-send the whole message), `replace` for a snapshot or terminal
 * (which carry the authoritative full text), `carry` for everything else (status/phase events touch no text).
 */
type StreamTextMerge = "append" | "replace" | "carry";

function streamTextMergeFor(eventType: string, isTerminal: boolean): StreamTextMerge {
	if (eventType === nodeChatStreamEventTypes.assistantDelta) {
		return "append";
	}

	return isTerminal || eventType === nodeChatStreamEventTypes.assistantSnapshot ? "replace" : "carry";
}

/**
 * Applies a `StreamTextMerge` to one text field. Returns undefined rather than "" when nothing is accumulated
 * yet, so an event that carries no reasoning leaves `reasoning` absent instead of seeding an empty segment.
 */
function mergeStreamText(
	merge: StreamTextMerge,
	existing: string | undefined,
	full: string | null | undefined,
	delta: string | null | undefined,
): string | undefined {
	if (merge === "append") {
		return delta ? `${existing ?? ""}${delta}` : existing;
	}

	return merge === "replace" ? (full ?? existing) : existing;
}

function maxSortOrder(messages: ChatMessageModel[]): number {
	return messages.reduce((max, message) => Math.max(max, message.sortOrder), 0);
}

function isoFromUnixMilliseconds(value: number | undefined): string {
	const date = new Date(value ?? Date.now());
	return Number.isNaN(date.getTime()) ? new Date().toISOString() : date.toISOString();
}

function replaceMessage(messages: ChatMessageModel[], nextMessage: ChatMessageModel): ChatMessageModel[] {
	const existingIndex = messages.findIndex((message) => message.id === nextMessage.id);
	if (existingIndex < 0) {
		return [...messages, nextMessage].toSorted(
			(left, right) => left.sortOrder - right.sortOrder || left.createdAt.localeCompare(right.createdAt),
		);
	}

	return messages.map((message, index) => (index === existingIndex ? nextMessage : message));
}

export function appendOptimisticNodeChatSend(
	conversation: ChatConversationModel,
	ids: OptimisticNodeChatSendIds,
	content: string,
	nowIso: string,
	model?: string,
	agentName?: string,
	reasoningEffort?: ReasoningEffort,
): ChatConversationModel {
	const nextSortOrder = maxSortOrder(conversation.messages) + 1;
	const userMessage: ChatMessageModel = {
		id: ids.userMessageId,
		conversationId: conversation.id,
		role: "user",
		content,
		status: "completed",
		createdAt: nowIso,
		updatedAt: nowIso,
		sortOrder: nextSortOrder,
	};
	const assistantMessage: ChatMessageModel = {
		id: ids.assistantMessageId,
		conversationId: conversation.id,
		role: "assistant",
		content: "",
		status: "pending",
		createdAt: nowIso,
		updatedAt: nowIso,
		sortOrder: nextSortOrder + 1,
		model,
		// Optimistically stamp the agent name and reasoning effort so the attribution row is fully visible
		// during streaming. The persisted values (from metadata_json) replace these on the post-stream refetch.
		agentName,
		reasoningEffort,
	};

	return {
		...conversation,
		updatedAt: nowIso,
		lastActivity: nowIso,
		lastMessagePreview: content,
		messages: [...conversation.messages, userMessage, assistantMessage],
	};
}

export function applyNodeChatStreamEvent(
	conversation: ChatConversationModel,
	event: NodeChatStreamEventDto,
): AppliedNodeChatStreamEvent {
	const terminalStatus = terminalStatusForEvent(event.type);
	const isTerminal = terminalStatus !== undefined;

	// Tool-lifecycle events feed the activity timeline only — they must NOT mutate assistant content or status.
	// The conversation is returned untouched and the streaming state is re-derived from the in-flight assistant
	// turn so the turn stays live (isActive) while tools run.
	if (isToolStreamEvent(event.type)) {
		const current = conversation.messages.find((message) => message.id === event.messageId && message.role === "assistant");
		// Merge the tool call into the ordered parts (collapsed requested→completed by tool-call id) so the in-flight
		// turn renders the tool card in its real wire slot and the result shows the instant the completed event lands.
		const toolCall = mapToolCallEvent(event);
		const { reasoningSegments, toolEntries, textSegments, noticeEntries } = decomposeParts(current?.parts);
		const nextToolEntries = toolCall ? mergeToolEntry(toolEntries, toolCall, event.sequence) : toolEntries;
		const nextParts = buildMessageParts(reasoningSegments, nextToolEntries, textSegments, noticeEntries);
		const nextConversation = current
			? { ...conversation, messages: replaceMessage(conversation.messages, { ...current, parts: nextParts }) }
			: conversation;
		return {
			conversation: nextConversation,
			streamingMessage: {
				conversationId: event.conversationId,
				messageId: event.messageId,
				content: current?.content ?? "",
				reasoning: current?.reasoning,
				parts: nextParts,
				startedAt: current?.createdAt ?? isoFromUnixMilliseconds(event.occurredAtUtc),
				isActive: true,
				inputTokens: current?.inputTokens,
				outputTokens: current?.outputTokens,
				totalTokens: current?.totalTokens,
				reasoningTokens: current?.reasoningTokens,
			},
			isTerminal: false,
			timelineEntry: toToolTimelineEntry(event),
		};
	}

	// A pending human prompt — a tool-approval request, or an `ask_user` question: flip the matching tool card into a
	// waiting state carrying the payload the inline controls post back (the approval request id, or the parsed
	// questions). Like tool-lifecycle events neither ever mutates assistant content/status — the turn stays live while
	// the operator decides/answers. No timeline entry is returned.
	if (event.type === nodeChatStreamEventTypes.approvalRequested || event.type === nodeChatStreamEventTypes.questionRequested) {
		const current = conversation.messages.find((message) => message.id === event.messageId && message.role === "assistant");
		const { reasoningSegments, toolEntries, textSegments, noticeEntries } = decomposeParts(current?.parts);
		const nextToolEntries = mergePendingPromptIntoToolEntries(
			toolEntries,
			event.toolCallId ?? undefined,
			event.toolName ?? undefined,
			event.sequence,
			event.type === nodeChatStreamEventTypes.questionRequested
				? { pendingQuestion: parsePendingUserQuestion(event) }
				: {
						pendingApprovalRequestId: event.approvalRequestId ?? undefined,
						pendingApprovalSessionScopeEligible: event.sessionScopeEligible ?? undefined,
					},
		);
		const nextParts = buildMessageParts(reasoningSegments, nextToolEntries, textSegments, noticeEntries);
		const nextConversation = current
			? { ...conversation, messages: replaceMessage(conversation.messages, { ...current, parts: nextParts }) }
			: conversation;
		return {
			conversation: nextConversation,
			streamingMessage: {
				conversationId: event.conversationId,
				messageId: event.messageId,
				content: current?.content ?? "",
				reasoning: current?.reasoning,
				parts: nextParts,
				startedAt: current?.createdAt ?? isoFromUnixMilliseconds(event.occurredAtUtc),
				isActive: true,
				inputTokens: current?.inputTokens,
				outputTokens: current?.outputTokens,
				totalTokens: current?.totalTokens,
				reasoningTokens: current?.reasoningTokens,
			},
			isTerminal: false,
		};
	}

	// A non-fatal turn notice (model substitution, tool disabled, history truncated): append a notice part to the
	// current assistant turn's ordered parts without touching `content`/`status` — the turn stays live afterward.
	// This is NOT a tool timeline entry, so no `timelineEntry` is returned; it only affects the message's parts.
	if (event.type === nodeChatStreamEventTypes.assistantNotice) {
		const current = conversation.messages.find((message) => message.id === event.messageId && message.role === "assistant");
		const { reasoningSegments, toolEntries, textSegments, noticeEntries } = decomposeParts(current?.parts);
		const nextNoticeEntries: NoticeEntryInput[] = [
			...noticeEntries,
			{
				id: `${event.messageId}:${event.sequence}`,
				sequence: event.sequence,
				noticeKind: event.noticeKind ?? "",
				text: event.noticeMessage ?? "",
				detail: event.noticeDetail ?? undefined,
			},
		];
		const nextParts = buildMessageParts(reasoningSegments, toolEntries, textSegments, nextNoticeEntries);
		const nextConversation = current
			? { ...conversation, messages: replaceMessage(conversation.messages, { ...current, parts: nextParts }) }
			: conversation;
		return {
			conversation: nextConversation,
			streamingMessage: {
				conversationId: event.conversationId,
				messageId: event.messageId,
				content: current?.content ?? "",
				reasoning: current?.reasoning,
				parts: nextParts,
				startedAt: current?.createdAt ?? isoFromUnixMilliseconds(event.occurredAtUtc),
				isActive: true,
				inputTokens: current?.inputTokens,
				outputTokens: current?.outputTokens,
				totalTokens: current?.totalTokens,
				reasoningTokens: current?.reasoningTokens,
			},
			isTerminal: false,
		};
	}

	// A pre-first-token runtime-phase transition: carry the phase forward on the streaming state without
	// touching content/status/parts, so the composer shows a "Loading model…" indicator during a local cold load.
	// The phase clears naturally once the first content delta lands (the main path returns no runtimePhase).
	if (event.type === nodeChatStreamEventTypes.assistantPhase) {
		const current = conversation.messages.find((message) => message.id === event.messageId && message.role === "assistant");
		return {
			conversation,
			streamingMessage: {
				conversationId: event.conversationId,
				messageId: event.messageId,
				content: current?.content ?? "",
				reasoning: current?.reasoning,
				parts: current?.parts,
				startedAt: current?.createdAt ?? isoFromUnixMilliseconds(event.occurredAtUtc),
				isActive: true,
				runtimePhase: event.runtimePhase ?? undefined,
				inputTokens: current?.inputTokens,
				outputTokens: current?.outputTokens,
				totalTokens: current?.totalTokens,
				reasoningTokens: current?.reasoningTokens,
			},
			isTerminal: false,
		};
	}

	// The local stream optimistically inserts the user message using the request's userMessageId.
	// Some backend stream versions report the assistant correlation id on the user-persisted event,
	// so treating that event as an assistant mutation would clobber the placeholder.
	if (event.type === nodeChatStreamEventTypes.userMessagePersisted) {
		const currentAssistant = conversation.messages.find(
			(message) => message.id === event.messageId && message.role === "assistant",
		);
		return {
			conversation,
			streamingMessage: {
				conversationId: event.conversationId,
				messageId: event.messageId,
				content: currentAssistant?.content ?? event.content ?? "",
				reasoning: currentAssistant?.reasoning ?? event.reasoning ?? undefined,
				parts: currentAssistant?.parts,
				startedAt: currentAssistant?.createdAt ?? isoFromUnixMilliseconds(event.occurredAtUtc),
				isActive: true,
				inputTokens: currentAssistant?.inputTokens ?? event.inputTokens ?? undefined,
				outputTokens: currentAssistant?.outputTokens ?? event.outputTokens ?? undefined,
				totalTokens: currentAssistant?.totalTokens ?? event.totalTokens ?? undefined,
				reasoningTokens: currentAssistant?.reasoningTokens ?? event.reasoningTokens ?? undefined,
			},
			isTerminal: false,
		};
	}

	const existing = conversation.messages.find((message) => message.id === event.messageId && message.role === "assistant");
	const isQueued = event.type === nodeChatStreamEventTypes.assistantQueued;
	const fallbackStatus: MessageStatus =
		terminalStatus ?? (isQueued ? "queued" : event.type === nodeChatStreamEventTypes.assistantPending ? "pending" : "streaming");
	const status = normalizeStatus(event.status, fallbackStatus);
	const eventTime = isoFromUnixMilliseconds(event.occurredAtUtc);
	// The wire is delta-only: an `assistant-delta` carries ONLY its delta and its `content`/`reasoning` are
	// never populated, so reading them on this branch would resurrect the full-snapshot-per-frame amplifier
	// (and, once the server stops sending them, silently blank the turn). A snapshot or a terminal carries the
	// authoritative full text and replaces the accumulation wholesale; every other event leaves the text alone.
	const textMerge = streamTextMergeFor(event.type, isTerminal);
	const content = mergeStreamText(textMerge, existing?.content, event.content, event.delta) ?? "";
	const reasoning = mergeStreamText(textMerge, existing?.reasoning, event.reasoning, event.reasoningDelta);
	const rebuiltParts = nextReasoningParts(existing, event, reasoning ?? undefined);
	// On a terminal turn, clear any lingering pending-approval waiting card: an API-tool DENY leaves a waiting card
	// with no completing tool-call event, and it must not survive the terminal into a dead "awaiting decision" prompt.
	const parts = isTerminal ? clearPendingPromptWaitingCards(rebuiltParts) : rebuiltParts;
	const assistantMessage: ChatMessageModel = {
		id: event.messageId,
		conversationId: event.conversationId,
		role: "assistant",
		content,
		reasoning: reasoning ?? undefined,
		parts,
		status,
		createdAt: existing?.createdAt ?? eventTime,
		updatedAt: eventTime,
		sortOrder: existing?.sortOrder ?? maxSortOrder(conversation.messages) + 1,
		model: event.model ?? existing?.model,
		// Stream events carry no agent attribution or reasoning effort, so always carry both forward from the
		// optimistic message (stamped in appendOptimisticNodeChatSend) — otherwise the rebuild drops them and
		// the attribution row falls back to "Default Assistant" until the post-stream refetch.
		agentName: existing?.agentName,
		agentDefinitionId: existing?.agentDefinitionId,
		reasoningEffort: existing?.reasoningEffort,
		error: event.error ?? undefined,
		inputTokens: event.inputTokens ?? existing?.inputTokens,
		outputTokens: event.outputTokens ?? existing?.outputTokens,
		totalTokens: event.totalTokens ?? existing?.totalTokens,
		reasoningTokens: event.reasoningTokens ?? existing?.reasoningTokens,
	};
	const nextConversation: ChatConversationModel = {
		...conversation,
		updatedAt: eventTime,
		lastActivity: eventTime,
		lastMessagePreview: content || conversation.lastMessagePreview,
		messages: replaceMessage(conversation.messages, assistantMessage),
	};

	return {
		conversation: nextConversation,
		streamingMessage: {
			conversationId: event.conversationId,
			messageId: event.messageId,
			content,
			reasoning: reasoning ?? undefined,
			parts,
			startedAt: assistantMessage.createdAt,
			isActive: !isTerminal,
			// Queued turns are live (isActive) but not yet streaming; clear once the streaming event arrives.
			isQueued: status === "queued",
			error: event.error ?? undefined,
			inputTokens: assistantMessage.inputTokens,
			outputTokens: assistantMessage.outputTokens,
			totalTokens: assistantMessage.totalTokens,
			reasoningTokens: assistantMessage.reasoningTokens,
		},
		isTerminal,
	};
}

export function markNodeChatStreamTerminated(
	conversation: ChatConversationModel,
	messageId: string,
	status: Extract<MessageStatus, "cancelled" | "failed" | "interrupted">,
	error?: string,
	failureCategory?: string,
): AppliedNodeChatStreamEvent {
	const nowIso = new Date().toISOString();
	const existing = conversation.messages.find((message) => message.id === messageId && message.role === "assistant");
	const assistantMessage: ChatMessageModel = {
		id: messageId,
		conversationId: conversation.id,
		role: "assistant",
		content: existing?.content ?? "",
		reasoning: existing?.reasoning,
		// This is always a terminal (cancelled/failed/interrupted) transition, so clear any lingering pending-approval
		// waiting card here too — the client-driven terminal path preserves parts verbatim and would otherwise keep a
		// dead Approve/Deny prompt alive until the refetch.
		parts: clearPendingPromptWaitingCards(existing?.parts),
		status,
		createdAt: existing?.createdAt ?? nowIso,
		updatedAt: nowIso,
		sortOrder: existing?.sortOrder ?? maxSortOrder(conversation.messages) + 1,
		model: existing?.model,
		// Carry agent attribution and reasoning effort forward so a cancelled/failed/interrupted terminal state
		// keeps the full attribution row intact until the post-stream refetch loads the persisted values.
		agentName: existing?.agentName,
		agentDefinitionId: existing?.agentDefinitionId,
		reasoningEffort: existing?.reasoningEffort,
		error,
		inputTokens: existing?.inputTokens,
		outputTokens: existing?.outputTokens,
		totalTokens: existing?.totalTokens,
		reasoningTokens: existing?.reasoningTokens,
	};
	const nextConversation = {
		...conversation,
		updatedAt: nowIso,
		lastActivity: nowIso,
		messages: replaceMessage(conversation.messages, assistantMessage),
	};

	return {
		conversation: nextConversation,
		streamingMessage: {
			conversationId: conversation.id,
			messageId,
			content: assistantMessage.content,
			reasoning: assistantMessage.reasoning,
			parts: assistantMessage.parts,
			startedAt: assistantMessage.createdAt,
			isActive: false,
			error,
			failureCategory,
			inputTokens: assistantMessage.inputTokens,
			outputTokens: assistantMessage.outputTokens,
			totalTokens: assistantMessage.totalTokens,
			reasoningTokens: assistantMessage.reasoningTokens,
		},
		isTerminal: true,
	};
}
