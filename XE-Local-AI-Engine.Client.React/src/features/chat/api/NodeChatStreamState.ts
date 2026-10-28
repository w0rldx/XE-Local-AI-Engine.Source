import { parsePendingUserQuestion } from "@/features/chat/api/AskUserQuestionWire";
import { mapToolCallEvent } from "@/features/chat/api/NodeChatMapper";
import {
	clearPendingPromptWaitingCards,
	decomposeParts,
	mergePendingPromptIntoToolEntries,
	mergeStreamText,
	mergeToolEntry,
	nextReasoningParts,
	splitInterimText,
	splitStreamedContent,
	type StreamTextMerge,
} from "@/features/chat/api/NodeChatStreamParts";
import type {
	ChatConversationModel,
	ChatMessageModel,
	ChatStreamingState,
	ChatTimelineEntry,
	MessageStatus,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";
import { buildMessageParts, type NoticeEntryInput } from "@/features/chat/models/MessageParts";
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

function streamTextMergeFor(eventType: string, isTerminal: boolean): StreamTextMerge {
	if (eventType === nodeChatStreamEventTypes.assistantDelta) {
		return "append";
	}

	return isTerminal || eventType === nodeChatStreamEventTypes.assistantSnapshot ? "replace" : "carry";
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
	// The prior streaming state, threaded by the stream loops. Only an assistant-snapshot reads it, to keep the
	// runtime phase alive across a snapshot (the phase is not on ChatMessageModel, so it cannot be re-derived).
	previous?: ChatStreamingState,
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
		const mergedToolEntries = toolCall ? mergeToolEntry(toolEntries, toolCall, event.sequence) : toolEntries;
		// A requested call carries the global content offset it was requested at: the text before it is interim
		// narration. The first offset starts tracking the whole streamed text, which `content` held until now.
		const contentOffset = event.type === nodeChatStreamEventTypes.toolCallRequested ? event.contentOffset : undefined;
		const nextToolEntries =
			toolCall && typeof contentOffset === "number"
				? mergedToolEntries.map((entry) =>
						entry.id === toolCall.id && entry.contentOffset === undefined ? { ...entry, contentOffset } : entry,
					)
				: mergedToolEntries;
		const streamedContent =
			current?.streamedContent ??
			(nextToolEntries.some((entry) => entry.contentOffset !== undefined) ? (current?.content ?? "") : undefined);
		const split = streamedContent === undefined ? undefined : splitInterimText(event.messageId, streamedContent, nextToolEntries);
		const nextContent = split?.content ?? current?.content ?? "";
		const nextParts = buildMessageParts(reasoningSegments, nextToolEntries, split?.textSegments ?? textSegments, noticeEntries);
		const nextMessage = current
			? { ...current, content: nextContent, parts: nextParts, ...(streamedContent === undefined ? {} : { streamedContent }) }
			: undefined;
		const nextConversation = nextMessage
			? { ...conversation, messages: replaceMessage(conversation.messages, nextMessage) }
			: conversation;
		return {
			conversation: nextConversation,
			streamingMessage: {
				conversationId: event.conversationId,
				messageId: event.messageId,
				content: nextContent,
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
						// The operator approves WHAT runs, so the prompt carries the call's arguments itself.
						args: event.arguments || undefined,
						pendingApprovalRequestId: event.approvalRequestId ?? undefined,
						pendingApprovalSessionScopeEligible: event.sessionScopeEligible ?? undefined,
						pendingWebReview: event.webReview ?? undefined,
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
	// The phase clears naturally once the first content delta or a terminal lands (the main path returns no
	// runtimePhase); a snapshot is the one exception and carries it forward from `previous`.
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
				runtimePhaseChangedAtUtc: event.runtimePhaseChangedAtUtc ?? undefined,
				runtimePhaseReason: event.runtimePhaseReason ?? undefined,
				runtimePhaseLastLoadMs: event.runtimePhaseLastLoadMs ?? undefined,
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
	const split = splitStreamedContent(existing, event, textMerge, isTerminal);
	const content = split?.content ?? mergeStreamText(textMerge, existing?.content, event.content, event.delta) ?? "";
	const reasoning = mergeStreamText(textMerge, existing?.reasoning, event.reasoning, event.reasoningDelta);
	const rebuiltParts = nextReasoningParts(existing, event, reasoning ?? undefined, split?.textSegments);
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
		...(split?.streamedContent === undefined ? {} : { streamedContent: split.streamedContent }),
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
		failureCategory: event.failureCategory ?? undefined,
		inputTokens: event.inputTokens ?? existing?.inputTokens,
		outputTokens: event.outputTokens ?? existing?.outputTokens,
		totalTokens: event.totalTokens ?? existing?.totalTokens,
		reasoningTokens: event.reasoningTokens ?? existing?.reasoningTokens,
		// Only the terminal event carries a snapshot; every other event keeps the one already on the message.
		contextWindow: event.contextWindow ?? existing?.contextWindow,
		// Same for the turn's throughput.
		throughput: event.throughput ?? existing?.throughput,
	};
	const nextConversation: ChatConversationModel = {
		...conversation,
		updatedAt: eventTime,
		lastActivity: eventTime,
		lastMessagePreview: content || conversation.lastMessagePreview,
		messages: replaceMessage(conversation.messages, assistantMessage),
	};

	// A snapshot re-states the turn's text; it is not a content delta, so it must NOT end the cold-load
	// affordance. Carry the phase and its server timestamp forward here only — content deltas and terminals
	// keep clearing both, which is what retires "Loading model…" once the first token (or the end) lands.
	const isSnapshot = event.type === nodeChatStreamEventTypes.assistantSnapshot;

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
			runtimePhase: isSnapshot ? previous?.runtimePhase : undefined,
			runtimePhaseChangedAtUtc: isSnapshot ? previous?.runtimePhaseChangedAtUtc : undefined,
			runtimePhaseReason: isSnapshot ? previous?.runtimePhaseReason : undefined,
			runtimePhaseLastLoadMs: isSnapshot ? previous?.runtimePhaseLastLoadMs : undefined,
			// Queued turns are live (isActive) but not yet streaming; clear once the streaming event arrives.
			isQueued: status === "queued",
			error: event.error ?? undefined,
			failureCategory: event.failureCategory ?? undefined,
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
		// This is always a terminal (canceled/failed/interrupted) transition, so clear any lingering pending-approval
		// waiting card here too — the client-driven terminal path preserves parts verbatim and would otherwise keep a
		// dead Approve/Deny prompt alive until the refetch.
		parts: clearPendingPromptWaitingCards(existing?.parts),
		status,
		createdAt: existing?.createdAt ?? nowIso,
		updatedAt: nowIso,
		sortOrder: existing?.sortOrder ?? maxSortOrder(conversation.messages) + 1,
		model: existing?.model,
		// Carry agent attribution and reasoning effort forward so a canceled/failed/interrupted terminal state
		// keeps the full attribution row intact until the post-stream refetch loads the persisted values.
		agentName: existing?.agentName,
		agentDefinitionId: existing?.agentDefinitionId,
		reasoningEffort: existing?.reasoningEffort,
		error,
		failureCategory,
		inputTokens: existing?.inputTokens,
		outputTokens: existing?.outputTokens,
		totalTokens: existing?.totalTokens,
		reasoningTokens: existing?.reasoningTokens,
		contextWindow: existing?.contextWindow,
		throughput: existing?.throughput,
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
