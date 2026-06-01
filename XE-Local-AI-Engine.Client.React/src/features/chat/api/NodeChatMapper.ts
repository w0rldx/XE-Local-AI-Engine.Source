import {
	nodeChatToolStreamEventTypes,
	type NodeChatConversationResponseDto,
	type NodeChatConversationSummaryResponseDto,
	type NodeChatMessageFeedbackResponseDto,
	type NodeChatMessagePartDto,
	type NodeChatMessageResponseDto,
	type NodeChatMessageRevisionsResponseDto,
	type NodeChatStreamEventDto,
} from "@/features/chat/api/NodeChatApi";
import type {
	ChatConversationModel,
	ChatFeedbackRating,
	ChatMessageFeedback,
	ChatMessageModel,
	ChatMessagePart,
	ChatMessageRevisions,
	ChatOrigin,
	ChatRole,
	ChatToolCall,
	MessageStatus,
	ToolCallState,
} from "@/features/chat/models/ChatModels";

const knownRoles = new Set<ChatRole>(["user", "assistant", "system", "tool"]);
const knownStatuses = new Set<MessageStatus>(["pending", "queued", "streaming", "completed", "cancelled", "failed", "interrupted"]);
const knownOrigins = new Set<ChatOrigin>(["local", "remote"]);
const knownRatings = new Set<ChatFeedbackRating>(["up", "down"]);
const knownToolStates = new Set<ToolCallState>(["requesting", "waiting", "received", "failed"]);

function toToolState(state: string | null | undefined): ToolCallState {
	const normalized = (state ?? "").toLowerCase() as ToolCallState;
	return knownToolStates.has(normalized) ? normalized : "received";
}

/**
 * Maps the wire `parts[]` to the ordered `ChatMessagePart[]`. Tool parts key on `toolCallId` (stable across
 * requested/completed); reasoning/text parts key on `${messageId}:${sequence}`. Unknown `kind` values are skipped
 * so a forward-compat backend addition never breaks rendering. Returns undefined when no usable parts remain.
 */
function mapParts(messageId: string, parts: NodeChatMessagePartDto[] | null | undefined): ChatMessagePart[] | undefined {
	if (!parts || parts.length === 0) {
		return undefined;
	}

	const mapped = parts.reduce<ChatMessagePart[]>((accumulator, part) => {
		const kind = part.kind?.toLowerCase();
		if (kind === "tool") {
			accumulator.push({
				kind: "tool",
				id: part.toolCallId ?? `${messageId}:${part.sequence}`,
				sequence: part.sequence,
				name: part.name ?? "tool",
				state: toToolState(part.state),
				args: part.args ?? undefined,
				result: part.result ?? undefined,
				requiresApproval: part.requiresApproval ?? undefined,
			});
		} else if (kind === "reasoning" || kind === "text") {
			accumulator.push({
				kind,
				id: `${messageId}:${part.sequence}`,
				sequence: part.sequence,
				text: part.text ?? "",
			});
		}

		return accumulator;
	}, []);

	return mapped.length > 0 ? mapped : undefined;
}

/**
 * Backward-compat synth for legacy turns persisted before ordered parts: a single leading reasoning segment from
 * the flat `reasoning` blob so the Thoughts block still renders. Tools are not recoverable from legacy metadata,
 * so they stay absent (matches the pre-fix reload behavior). Returns undefined when there is nothing to show.
 */
function synthesizeLegacyParts(messageId: string, reasoning: string | null | undefined): ChatMessagePart[] | undefined {
	if (!reasoning || reasoning.trim().length === 0) {
		return undefined;
	}

	// Keyed on the bare message id (not `${messageId}:0`) so a reloaded legacy turn matches the component-level synth
	// and keeps the reasoning controls' stable testids.
	return [{ kind: "reasoning", id: messageId, sequence: 0, text: reasoning }];
}

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
		// Prefer the persisted ordered parts so a reload restores the exact reasoning↔tool interleave; legacy turns
		// (no parts) synthesize a single Thoughts block from the flat reasoning blob so they still render.
		parts: mapParts(dto.messageId, dto.parts) ?? synthesizeLegacyParts(dto.messageId, dto.reasoning),
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
		// Feedback travels on the message (feedback flow): map rating only when present (null = no feedback), so the
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
		selectedPath: dto.selectedPath ?? undefined,
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
 * Maps a tool-lifecycle stream event into the `ChatToolCall` shape the stream reducer folds into ordered parts.
 * Returns null for non-tool events. `tool-call-requested` → `waiting` when the tool needs approval (beta ships none) else
 * `requesting`; `tool-call-completed` → `failed` when `isError` else `received`. The tool call id is the stable
 * key so a completed event can later collapse onto its requested entry.
 */
export function mapToolCallEvent(event: NodeChatStreamEventDto): ChatToolCall | null {
	if (event.type === nodeChatToolStreamEventTypes.toolCallRequested) {
		const requiresApproval = event.requiresApproval ?? false;
		return {
			// Fall back to a per-event unique key (messageId + sequence) so two distinct tool calls in one turn that
			// both lack an explicit tool-call id are never collapsed onto the same card.
			id: event.toolCallId ?? `${event.messageId}:${event.sequence}`,
			name: event.toolName ?? "tool",
			state: requiresApproval ? "waiting" : "requesting",
			args: event.arguments ?? undefined,
			requiresApproval,
		};
	}

	if (event.type === nodeChatToolStreamEventTypes.toolCallCompleted) {
		return {
			id: event.toolCallId ?? `${event.messageId}:${event.sequence}`,
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
