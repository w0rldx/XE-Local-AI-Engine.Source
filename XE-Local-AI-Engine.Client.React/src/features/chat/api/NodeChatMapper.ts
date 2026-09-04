import type {
	XeLocalAiEngineClientEndpointsLocalChatV1NodeChatConversationResponse,
	XeLocalAiEngineClientEndpointsLocalChatV1NodeChatConversationSummaryResponse,
	XeLocalAiEngineClientEndpointsLocalChatV1NodeChatMessageFeedbackResponse,
	XeLocalAiEngineClientEndpointsLocalChatV1NodeChatMessageResponse,
	XeLocalAiEngineClientEndpointsLocalChatV1NodeChatMessageRevisionsResponse,
	XeLocalAiEngineClientServicesChatNodeChatMessagePart,
	XeLocalAiEngineClientServicesChatNodeChatMessageSource,
} from "@/core/api/generated";
import { persistableReasoningEfforts } from "@/features/chat/stores/NodeChatPreferencesStore";
import type {
	ChatConversationModel,
	ChatFeedbackRating,
	ChatMessageFeedback,
	ChatMessageModel,
	ChatMessagePart,
	ChatMessageRevisions,
	ChatMessageSource,
	ChatOrigin,
	ChatRole,
	ChatToolCall,
	MessageStatus,
	ReasoningEffort,
	ToolCallState,
} from "@/features/chat/models/ChatModels";
import { type NodeChatStreamEventDto, nodeChatToolStreamEventTypes } from "@/features/chat/models/NodeChatStreamTypes";

// Local aliases for the generated REST response types (the backend OpenAPI is the single source of truth). Every
// generated field is optional (`x?: T`), so each mapper coalesces missing values to the prior default below.
type NodeChatConversationResponseDto = XeLocalAiEngineClientEndpointsLocalChatV1NodeChatConversationResponse;
type NodeChatConversationSummaryResponseDto = XeLocalAiEngineClientEndpointsLocalChatV1NodeChatConversationSummaryResponse;
type NodeChatMessageResponseDto = XeLocalAiEngineClientEndpointsLocalChatV1NodeChatMessageResponse;
type NodeChatMessagePartDto = XeLocalAiEngineClientServicesChatNodeChatMessagePart;
type NodeChatMessageSourceDto = XeLocalAiEngineClientServicesChatNodeChatMessageSource;
type NodeChatMessageRevisionsResponseDto = XeLocalAiEngineClientEndpointsLocalChatV1NodeChatMessageRevisionsResponse;
type NodeChatMessageFeedbackResponseDto = XeLocalAiEngineClientEndpointsLocalChatV1NodeChatMessageFeedbackResponse;

const knownRoles = new Set<ChatRole>(["user", "assistant", "system", "tool"]);
const knownStatuses = new Set<MessageStatus>([
	"pending",
	"queued",
	"streaming",
	"completed",
	"cancelled",
	"failed",
	"interrupted",
]);
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
		const sequence = part.sequence ?? 0;
		if (kind === "tool") {
			accumulator.push({
				kind: "tool",
				id: part.toolCallId ?? `${messageId}:${sequence}`,
				sequence,
				name: part.name ?? "tool",
				state: toToolState(part.state),
				args: part.args ?? undefined,
				result: part.result ?? undefined,
				requiresApproval: part.requiresApproval ?? undefined,
			});
		} else if (kind === "reasoning" || kind === "text") {
			accumulator.push({
				kind,
				id: `${messageId}:${sequence}`,
				sequence,
				text: part.text ?? "",
			});
		} else if (kind === "notice") {
			accumulator.push({
				kind: "notice",
				id: `${messageId}:${sequence}`,
				sequence,
				noticeKind: part.name ?? "",
				text: part.text ?? "",
				// A notice part reuses the generic `state` member for its structured detail, the way it reuses `name`
				// for the notice kind — the backend accumulator persists it there, with no field of its own.
				detail: part.state ?? undefined,
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

/**
 * Maps the wire knowledge-base `sources[]` to `ChatMessageSource[]`. Drops entries with no id/title
 * so a malformed record never renders a blank source card. Returns undefined when nothing usable remains, so the
 * "Sources" strip stays hidden for legacy turns, non-knowledge turns, and user messages.
 */
function mapSources(sources: NodeChatMessageSourceDto[] | null | undefined): ChatMessageSource[] | undefined {
	if (!sources || sources.length === 0) {
		return undefined;
	}

	const mapped: ChatMessageSource[] = [];
	for (const source of sources) {
		const documentId = source.documentId ?? "";
		const chunkId = source.chunkId ?? "";
		const title = source.title ?? "";
		if (documentId.length === 0 || title.length === 0) {
			continue;
		}

		mapped.push({
			documentId,
			chunkId,
			title,
			section: source.section ?? undefined,
			// int64/double wire values arrive as numbers; Number() is a defensive normalize against a stray bigint.
			score: source.score != null ? Number(source.score) : 0,
		});
	}

	return mapped.length > 0 ? mapped : undefined;
}

function mapMessage(dto: NodeChatMessageResponseDto): ChatMessageModel {
	const messageId = dto.messageId ?? "";
	return {
		id: messageId,
		conversationId: dto.conversationId ?? "",
		requestId: dto.requestId ?? undefined,
		role: toRole(dto.role ?? ""),
		content: dto.content ?? "",
		reasoning: dto.reasoning ?? undefined,
		// Prefer the persisted ordered parts so a reload restores the exact reasoning↔tool interleave; legacy turns
		// (no parts) synthesize a single Thoughts block from the flat reasoning blob so they still render.
		parts: mapParts(messageId, dto.parts) ?? synthesizeLegacyParts(messageId, dto.reasoning),
		status: toStatus(dto.status ?? ""),
		createdAt: toIso(dto.createdAtUtc ?? 0),
		updatedAt: toIso(dto.updatedAtUtc ?? 0),
		sortOrder: dto.sequence ?? 0,
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
		// Agent attribution fields stamped at send time (ride the metadata_json blob as trailing members).
		// Absent for legacy turns (null on the wire → undefined here); ChatMessage falls back to "Default Assistant".
		agentName: dto.agentName ?? undefined,
		agentDefinitionId: dto.agentDefinitionId ?? undefined,
		// Effective reasoning effort used at generation time (persisted in metadata_json, same blob as agentName).
		// null on wire = legacy turn or no effort recorded → undefined. Unknown/malformed values also → undefined
		// (narrowed against the known union; a stale server value must not corrupt the client model).
		reasoningEffort:
			dto.reasoningEffort != null && persistableReasoningEfforts.includes(dto.reasoningEffort as ReasoningEffort)
				? (dto.reasoningEffort as ReasoningEffort)
				: undefined,
		// Backend-exact generation duration. The int64 wire contract is pinned to a JSON number (the generated zod
		// validates it as `z.int()`, matching the TS `number` type), so this is already a number; the Number() cast is
		// now a defensive no-op that also normalizes any stray bigint before the downstream tps math (outputTokens /
		// durationMs) — which would otherwise throw "Cannot mix BigInt and other types".
		generationDurationMs: dto.generationDurationMs != null ? Number(dto.generationDurationMs) : undefined,
		// Knowledge-base sources that grounded this turn (persisted in metadata_json, same blob as the attribution
		// fields). Null/absent on the wire → undefined; empty → undefined so the Sources strip stays hidden.
		sources: mapSources(dto.sources),
	};
}

export function mapConversationSummary(dto: NodeChatConversationSummaryResponseDto): ChatConversationModel {
	const lastSeen = toIso(dto.lastSeenUtc ?? 0);

	return {
		id: dto.conversationId ?? "",
		title: titleOrFallback(dto.title),
		createdAt: toIso(dto.createdAtUtc ?? 0),
		updatedAt: lastSeen,
		lastActivity: lastSeen,
		lastMessagePreview: dto.lastMessagePreview ?? undefined,
		isPinned: dto.isPinned ?? false,
		isArchived: dto.archived ?? false,
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
		if ((message.content ?? "").trim().length === 0) {
			continue;
		}

		if (!latest || (message.sequence ?? 0) > (latest.sequence ?? 0)) {
			latest = message;
		}
	}

	if (!latest) {
		return undefined;
	}

	const normalized = (latest.content ?? "").replace(/\s+/g, " ").trim();
	return normalized.length > MAX_PREVIEW_LENGTH ? `${normalized.slice(0, MAX_PREVIEW_LENGTH - 1)}…` : normalized;
}

export function mapConversation(dto: NodeChatConversationResponseDto): ChatConversationModel {
	const lastSeen = toIso(dto.lastSeenUtc ?? 0);
	const messages = dto.messages ?? [];

	return {
		id: dto.conversationId ?? "",
		title: titleOrFallback(dto.title),
		createdAt: toIso(dto.createdAtUtc ?? 0),
		updatedAt: lastSeen,
		lastActivity: lastSeen,
		lastMessagePreview: previewFromMessages(messages),
		isPinned: dto.isPinned ?? false,
		isArchived: dto.archived ?? false,
		memoryExcluded: dto.memoryExcluded ?? false,
		origin: toOrigin(dto.origin),
		branchOfConversationId: dto.branchOfConversationId ?? undefined,
		selectedPath: dto.selectedPath ?? undefined,
		messages: messages.map(mapMessage),
	};
}

function toRating(rating: string): ChatFeedbackRating {
	const normalized = rating.toLowerCase() as ChatFeedbackRating;
	return knownRatings.has(normalized) ? normalized : "up";
}

export function mapMessageRevisions(dto: NodeChatMessageRevisionsResponseDto): ChatMessageRevisions {
	return {
		messageId: dto.messageId ?? "",
		variantGroupId: dto.variantGroupId ?? undefined,
		variants: (dto.variants ?? []).map(mapMessage).toSorted((left, right) => left.sortOrder - right.sortOrder),
	};
}

/**
 * Maps a tool-lifecycle stream event into the `ChatToolCall` shape the stream reducer folds into ordered parts.
 * Returns null for non-tool events. `tool-call-requested` → `waiting` when the tool needs approval, else
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
		messageId: dto.messageId ?? "",
		conversationId: dto.conversationId ?? "",
		rating: toRating(dto.rating ?? ""),
		comment: dto.comment ?? undefined,
		createdAt: toIso(dto.createdAtUtc ?? 0),
		updatedAt: toIso(dto.updatedAtUtc ?? 0),
	};
}
