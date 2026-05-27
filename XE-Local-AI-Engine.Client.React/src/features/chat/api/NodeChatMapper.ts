import {
	nodeChatToolStreamEventTypes,
	type NodeChatConversationResponseDto,
	type NodeChatConversationSummaryResponseDto,
	type NodeChatMessageFeedbackResponseDto,
	type NodeChatMessageResponseDto,
	type NodeChatMessageRevisionsResponseDto,
	type NodeChatStreamEventDto,
} from "@/features/chat/api/NodeChatApi";
import type {
	ChatConversationModel,
	ChatFeedbackRating,
	ChatMessageFeedback,
	ChatMessageModel,
	ChatMessageRevisions,
	ChatOrigin,
	ChatRole,
	ChatToolCall,
	MessageStatus,
} from "@/features/chat/models/ChatModels";

const knownRoles = new Set<ChatRole>(["user", "assistant", "system", "tool"]);
const knownStatuses = new Set<MessageStatus>(["pending", "queued", "streaming", "completed", "cancelled", "failed", "interrupted"]);
const knownOrigins = new Set<ChatOrigin>(["local", "remote"]);
const knownRatings = new Set<ChatFeedbackRating>(["up", "down"]);

function toIso(unixMilliseconds: number): string {
	const date = new Date(unixMilliseconds);
	return Number.isNaN(date.getTime()) ? new Date(0).toISOString() : date.toISOString();
}

function toRole(role: string): ChatRole {
	const normalized = role.toLowerCase() as ChatRole;
	return knownRoles.has(normalized) ? normalized : "assistant";
}

function toStatus(status: string): MessageStatus {
	const normalized = status.toLowerCase() as MessageStatus;
	return knownStatuses.has(normalized) ? normalized : "completed";
}

function toOrigin(origin: string | null | undefined): ChatOrigin {
	const normalized = (origin ?? "").toLowerCase() as ChatOrigin;
	return knownOrigins.has(normalized) ? normalized : "local";
}

function titleOrFallback(title: string | null | undefined): string {
	return title?.trim() || "Untitled conversation";
}

export function mapMessage(dto: NodeChatMessageResponseDto): ChatMessageModel {
	return {
		id: dto.messageId,
		conversationId: dto.conversationId,
		requestId: dto.requestId ?? undefined,
		role: toRole(dto.role),
		content: dto.content,
		reasoning: dto.reasoning ?? undefined,
		status: toStatus(dto.status),
		createdAt: toIso(dto.createdAtUtc),
		updatedAt: toIso(dto.updatedAtUtc),
		sortOrder: dto.sequence,
		model: dto.model ?? undefined,
		error: dto.error ?? undefined,
		origin: toOrigin(dto.origin),
		inputTokens: dto.inputTokens ?? undefined,
		outputTokens: dto.outputTokens ?? undefined,
		totalTokens: dto.totalTokens ?? undefined,
		reasoningTokens: dto.reasoningTokens ?? undefined,
		parentMessageId: dto.parentMessageId ?? undefined,
		variantGroupId: dto.variantGroupId ?? undefined,
		// Feedback travels on the message (Phase 5.3): map rating only when present (null = no feedback), so the
		// control stays neutral instead of defaulting to a thumbs-up. Comment is dropped when there is no rating.
		feedbackRating: dto.feedbackRating != null ? toRating(dto.feedbackRating) : undefined,
		feedbackComment: dto.feedbackRating != null ? (dto.feedbackComment ?? undefined) : undefined,
	};
}

export function mapConversationSummary(dto: NodeChatConversationSummaryResponseDto): ChatConversationModel {
	const lastSeen = toIso(dto.lastSeenUtc);

	return {
		id: dto.conversationId,
		title: titleOrFallback(dto.title),
		createdAt: toIso(dto.createdAtUtc),
		updatedAt: lastSeen,
		lastActivity: lastSeen,
		lastMessagePreview: dto.lastMessagePreview ?? undefined,
		isPinned: dto.isPinned,
		isArchived: dto.archived,
		origin: toOrigin(dto.origin),
		messages: [],
	};
}

// The summary list carries a server-computed lastMessagePreview; the full conversation payload does not. Derive
// an equivalent preview from the loaded messages so that when the selected conversation is merged into the list
// (mergeSelectedConversation swaps the summary for the full model) its list item keeps a preview instead of
// collapsing to "No messages". Undefined for a genuinely empty conversation, which correctly shows "No messages".
const MAX_PREVIEW_LENGTH = 120;
function previewFromMessages(messages: NodeChatMessageResponseDto[]): string | undefined {
	let latest: NodeChatMessageResponseDto | undefined;
	for (const message of messages) {
		if (message.content.trim().length === 0) {
			continue;
		}

		if (!latest || message.sequence > latest.sequence) {
			latest = message;
		}
	}

	if (!latest) {
		return undefined;
	}

	const normalized = latest.content.replace(/\s+/g, " ").trim();
	return normalized.length > MAX_PREVIEW_LENGTH ? `${normalized.slice(0, MAX_PREVIEW_LENGTH - 1)}…` : normalized;
}

export function mapConversation(dto: NodeChatConversationResponseDto): ChatConversationModel {
	const lastSeen = toIso(dto.lastSeenUtc);

	return {
		id: dto.conversationId,
		title: titleOrFallback(dto.title),
		createdAt: toIso(dto.createdAtUtc),
		updatedAt: lastSeen,
		lastActivity: lastSeen,
		lastMessagePreview: previewFromMessages(dto.messages),
		isPinned: dto.isPinned,
		isArchived: dto.archived,
		origin: toOrigin(dto.origin),
		branchOfConversationId: dto.branchOfConversationId ?? undefined,
		messages: dto.messages.map(mapMessage),
	};
}

function toRating(rating: string): ChatFeedbackRating {
	const normalized = rating.toLowerCase() as ChatFeedbackRating;
	return knownRatings.has(normalized) ? normalized : "up";
}

export function mapMessageRevisions(dto: NodeChatMessageRevisionsResponseDto): ChatMessageRevisions {
	return {
		messageId: dto.messageId,
		variantGroupId: dto.variantGroupId ?? undefined,
		variants: dto.variants.map(mapMessage).toSorted((left, right) => left.sortOrder - right.sortOrder),
	};
}

/**
 * Maps a tool-lifecycle stream event into the `ChatToolCall` shape `ToolCallDisplay` renders. Returns null for
 * non-tool events. `tool-call-requested` → `waiting` when the tool needs approval (beta ships none) else
 * `requesting`; `tool-call-completed` → `failed` when `isError` else `received`. The tool call id is the stable
 * key so a completed event can later collapse onto its requested entry.
 */
export function mapToolCallEvent(event: NodeChatStreamEventDto): ChatToolCall | null {
	if (event.type === nodeChatToolStreamEventTypes.toolCallRequested) {
		const requiresApproval = event.requiresApproval ?? false;
		return {
			id: event.toolCallId ?? event.messageId,
			name: event.toolName ?? "tool",
			state: requiresApproval ? "waiting" : "requesting",
			args: event.arguments ?? undefined,
			requiresApproval,
		};
	}

	if (event.type === nodeChatToolStreamEventTypes.toolCallCompleted) {
		return {
			id: event.toolCallId ?? event.messageId,
			name: event.toolName ?? "tool",
			state: event.isError ? "failed" : "received",
			result: event.result ?? undefined,
		};
	}

	return null;
}

export function mapMessageFeedback(dto: NodeChatMessageFeedbackResponseDto): ChatMessageFeedback {
	return {
		messageId: dto.messageId,
		conversationId: dto.conversationId,
		rating: toRating(dto.rating),
		comment: dto.comment ?? undefined,
		createdAt: toIso(dto.createdAtUtc),
		updatedAt: toIso(dto.updatedAtUtc),
	};
}
