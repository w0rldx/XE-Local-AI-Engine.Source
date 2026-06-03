import { mapToolCallEvent } from "@/features/chat/api/NodeChatMapper";
import type {
	ChatConversationModel,
	ChatMessageModel,
	ChatMessagePart,
	ChatStreamingState,
	ChatTimelineEntry,
	ChatToolCall,
	MessageStatus,
} from "@/features/chat/models/ChatModels";
import {
	buildMessageParts,
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
	assistantDelta: "assistant-delta",
	assistantCompleted: "assistant-completed",
	assistantCancelled: "assistant-cancelled",
	assistantFailed: "assistant-failed",
	assistantInterrupted: "assistant-interrupted",
	// Tool lifecycle events (Phase D6) reuse the dedicated tool-event constant so the wire names stay DRY.
	toolCallRequested: nodeChatToolStreamEventTypes.toolCallRequested,
	toolCallCompleted: nodeChatToolStreamEventTypes.toolCallCompleted,
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
 * Splits the prior ordered `parts[]` back into reasoning segments, tool entries, and text segments so the
 * accumulator can extend them without losing any existing interleave. All three kinds must round-trip so the
 * "byte-identical live vs reload" invariant holds for resumed/re-attached turns that carry text parts.
 */
function decomposeParts(parts: readonly ChatMessagePart[] | undefined): {
	reasoningSegments: ReasoningSegmentInput[];
	toolEntries: ToolEntryInput[];
	textSegments: TextSegmentInput[];
} {
	const reasoningSegments: ReasoningSegmentInput[] = [];
	const toolEntries: ToolEntryInput[] = [];
	const textSegments: TextSegmentInput[] = [];
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
			});
		} else if (part.kind === "text") {
			textSegments.push({ id: part.id, sequence: part.sequence, text: part.text });
		}
	}

	return { reasoningSegments, toolEntries, textSegments };
}

/** The highest `sequence` across all accumulated parts — i.e. the wire position of the most recent part. */
function maxPartSequence(
	reasoningSegments: readonly ReasoningSegmentInput[],
	toolEntries: readonly ToolEntryInput[],
	textSegments: readonly TextSegmentInput[] = [],
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

	return max;
}

/**
 * Folds a reasoning delta into the ordered segments: when the most recent part is the trailing reasoning run the
 * delta extends it; when the most recent part is a tool or text (or there is no segment yet) a NEW reasoning segment
 * opens at this event's `sequence` — this is what produces the second Thoughts block after a tool (Option A interleave).
 */
function appendReasoningDelta(
	reasoningSegments: ReasoningSegmentInput[],
	toolEntries: readonly ToolEntryInput[],
	textSegments: readonly TextSegmentInput[],
	messageId: string,
	sequence: number,
	delta: string,
): ReasoningSegmentInput[] {
	const trailing = reasoningSegments.at(-1);
	const lastIsTrailingReasoning =
		trailing !== undefined && trailing.sequence >= maxPartSequence(reasoningSegments, toolEntries, textSegments);

	if (lastIsTrailingReasoning && trailing) {
		return reasoningSegments.map((segment, index) =>
			index === reasoningSegments.length - 1 ? { ...segment, text: `${segment.text}${delta}` } : segment,
		);
	}

	return [...reasoningSegments, { id: `${messageId}:${sequence}`, sequence, text: delta }];
}

/** Merges a tool call (collapsed requested→completed by id) into the ordered tool entries, preserving its slot. */
function mergeToolEntry(toolEntries: readonly ToolEntryInput[], toolCall: ChatToolCall, sequence: number): ToolEntryInput[] {
	const existingIndex = toolEntries.findIndex((entry) => entry.id === toolCall.id);
	if (existingIndex < 0) {
		return [
			...toolEntries,
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

	return toolEntries.map((entry, index) =>
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
	const { reasoningSegments, toolEntries, textSegments } = decomposeParts(existing?.parts);

	let nextReasoningSegments = reasoningSegments;
	if (event.reasoningDelta) {
		nextReasoningSegments = appendReasoningDelta(
			reasoningSegments,
			toolEntries,
			textSegments,
			event.messageId,
			event.sequence,
			event.reasoningDelta,
		);
	} else if (reasoning && reasoningSegments.length === 0) {
		// A full reasoning value with no prior segment (e.g. terminal/resume rehydrate): seed one leading segment.
		nextReasoningSegments = [{ id: `${event.messageId}:${event.sequence}`, sequence: event.sequence, text: reasoning }];
	}

	if (nextReasoningSegments.length === 0 && toolEntries.length === 0 && textSegments.length === 0) {
		return undefined;
	}

	return buildMessageParts(nextReasoningSegments, toolEntries, textSegments);
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
		// Optimistically stamp the agent name so the attribution row is visible during streaming.
		// The persisted name (from metadata_json) replaces this on the post-stream refetch.
		agentName,
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
		const { reasoningSegments, toolEntries, textSegments } = decomposeParts(current?.parts);
		const nextToolEntries = toolCall ? mergeToolEntry(toolEntries, toolCall, event.sequence) : toolEntries;
		const nextParts = buildMessageParts(reasoningSegments, nextToolEntries, textSegments);
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
	const content = event.content ?? `${existing?.content ?? ""}${event.delta ?? ""}`;
	const reasoning =
		event.reasoning ?? (event.reasoningDelta ? `${existing?.reasoning ?? ""}${event.reasoningDelta}` : existing?.reasoning);
	const parts = nextReasoningParts(existing, event, reasoning ?? undefined);
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
		parts: existing?.parts,
		status,
		createdAt: existing?.createdAt ?? nowIso,
		updatedAt: nowIso,
		sortOrder: existing?.sortOrder ?? maxSortOrder(conversation.messages) + 1,
		model: existing?.model,
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
