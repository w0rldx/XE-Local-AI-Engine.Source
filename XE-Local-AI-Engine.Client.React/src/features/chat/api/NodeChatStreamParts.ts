import type { ChatMessageModel, ChatMessagePart, ChatToolCall } from "@/features/chat/models/ChatModels";
import {
	buildMessageParts,
	type NoticeEntryInput,
	type ReasoningSegmentInput,
	type TextSegmentInput,
	type ToolEntryInput,
} from "@/features/chat/models/MessageParts";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";

// The pure accumulator behind applyNodeChatStreamEvent: folds stream events into an assistant turn's ordered
// `parts[]` (reasoning segments, tool cards, interim text, notices) and its streamed text.

/**
 * Splits the prior ordered `parts[]` back into reasoning segments, tool entries, text segments, and notice entries
 * so the accumulator can extend them without losing any existing interleave. All four kinds must round-trip so the
 * "byte-identical live vs reload" invariant holds for resumed/re-attached turns that carry text/notice parts.
 */
export function decomposeParts(parts: readonly ChatMessagePart[] | undefined): {
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
				pendingWebReview: part.pendingWebReview,
				pendingQuestion: part.pendingQuestion,
				images: part.images,
				contentOffset: part.contentOffset,
			});
		} else if (part.kind === "text") {
			textSegments.push({ id: part.id, sequence: part.sequence, text: part.text });
		} else if (part.kind === "notice") {
			noticeEntries.push({
				id: part.id,
				sequence: part.sequence,
				noticeKind: part.noticeKind,
				text: part.text,
				detail: part.detail,
			});
		}
	}

	return { reasoningSegments, toolEntries, textSegments, noticeEntries };
}

/**
 * Re-derives the interim `text` segments and the answer from the whole streamed text: each distinct tool
 * `contentOffset` closes the text since the previous one into a segment sharing that call's `sequence`, and only the
 * text after the last offset stays the answer. Derived rather than accumulated, so a delta that trails its tool event,
 * a snapshot replacement and a resume replay all land on the same split the backend persists.
 */
export function splitInterimText(
	messageId: string,
	streamedContent: string,
	toolEntries: readonly ToolEntryInput[],
): { textSegments: TextSegmentInput[]; content: string } {
	const boundaries = new Map<number, number>();
	for (const entry of toolEntries) {
		const offset = entry.contentOffset;
		if (offset !== undefined && offset > 0) {
			boundaries.set(offset, Math.min(boundaries.get(offset) ?? Number.POSITIVE_INFINITY, entry.sequence));
		}
	}

	const textSegments: TextSegmentInput[] = [];
	let start = 0;
	for (const [offset, sequence] of [...boundaries].toSorted(([left], [right]) => left - right)) {
		textSegments.push({ id: `${messageId}:text:${offset}`, sequence, text: streamedContent.slice(start, offset) });
		start = offset;
	}

	return { textSegments, content: streamedContent.slice(start) };
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
export function mergeToolEntry(
	toolEntries: readonly ToolEntryInput[],
	toolCall: ChatToolCall,
	sequence: number,
): ToolEntryInput[] {
	// A prompt replay that cannot be correlated to a persisted call is represented by a generic request-id-keyed card.
	// Once a terminal tool event arrives, replace that fallback with the real call/result rather than leaving an empty
	// generic card beside it. Request-id-keyed entries are distinguishable from correlated cards because their id is
	// exactly their pending approval/question request id.
	const resolved = toolCall.state === "received" || toolCall.state === "failed" || toolCall.state === "denied";
	const entriesWithoutResolvedGenericPrompt = resolved
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
					pendingApprovalRequestId: resolved ? undefined : entry.pendingApprovalRequestId,
					pendingApprovalSessionScopeEligible: resolved ? undefined : entry.pendingApprovalSessionScopeEligible,
					pendingWebReview: resolved ? undefined : entry.pendingWebReview,
					// Same rule for an `ask_user` question: once the tool call resolves, the answer has been consumed
					// (or the wait timed out), so the inline question card must not survive on the resolved card.
					pendingQuestion: resolved ? undefined : entry.pendingQuestion,
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
export function mergePendingPromptIntoToolEntries(
	toolEntries: readonly ToolEntryInput[],
	callId: string | undefined,
	toolName: string | undefined,
	sequence: number,
	prompt: Pick<
		ToolEntryInput,
		"args" | "pendingApprovalRequestId" | "pendingApprovalSessionScopeEligible" | "pendingWebReview" | "pendingQuestion"
	>,
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
					// A replay without arguments (an older node, a metadata-poor resume) keeps the args the card has.
					args: prompt.args ?? entry.args,
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
export function nextReasoningParts(
	existing: ChatMessageModel | undefined,
	event: NodeChatStreamEventDto,
	reasoning: string | undefined,
	derivedTextSegments?: TextSegmentInput[],
): ChatMessagePart[] | undefined {
	const decomposed = decomposeParts(existing?.parts);
	const { reasoningSegments, toolEntries, noticeEntries } = decomposed;
	const textSegments = derivedTextSegments ?? decomposed.textSegments;

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
 * Approve/Deny controls until the post-stream refetch scrubs it; a question whose turn was canceled or timed out is
 * the same shape. Once the turn is terminal the prompt can no longer be answered, so the card is flipped to `failed`
 * and its prompt cleared. Returns the same array reference when nothing changed so unaffected turns keep referential
 * stability.
 */
export function clearPendingPromptWaitingCards(parts: ChatMessagePart[] | undefined): ChatMessagePart[] | undefined {
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
				pendingWebReview: undefined,
				pendingQuestion: undefined,
			};
		}

		return part;
	});

	return changed ? next : parts;
}

/**
 * For a turn whose tool calls split its text (`streamedContent` is tracked): folds the event into the whole streamed
 * text and re-derives the answer and the interim segments. A terminal re-states the persisted answer; when it is not
 * the answer this split produced (an interrupted turn keeps its whole text), the server wins and the segments drop.
 * Undefined for a turn with no split, which keeps the plain `content` merge.
 */
export function splitStreamedContent(
	existing: ChatMessageModel | undefined,
	event: NodeChatStreamEventDto,
	textMerge: StreamTextMerge,
	isTerminal: boolean,
): { content: string; textSegments: TextSegmentInput[]; streamedContent?: string } | undefined {
	if (existing?.streamedContent === undefined) {
		return undefined;
	}

	const { toolEntries } = decomposeParts(existing.parts);
	if (isTerminal) {
		const split = splitInterimText(event.messageId, existing.streamedContent, toolEntries);
		const persisted = event.content ?? split.content;
		return persisted === split.content ? split : { content: persisted, textSegments: [] };
	}

	const streamedContent = mergeStreamText(textMerge, existing.streamedContent, event.content, event.delta) ?? "";
	return { ...splitInterimText(event.messageId, streamedContent, toolEntries), streamedContent };
}

/**
 * How an assistant lifecycle event's text fields fold into the accumulated turn, under the delta-only wire
 * contract: `append` for a delta (which carries ONLY its delta — its `content`/`reasoning` are never populated,
 * and reading them is what made every frame re-send the whole message), `replace` for a snapshot or terminal
 * (which carry the authoritative full text), `carry` for everything else (status/phase events touch no text).
 */
export type StreamTextMerge = "append" | "replace" | "carry";

/**
 * Applies a `StreamTextMerge` to one text field. Returns undefined rather than "" when nothing is accumulated
 * yet, so an event that carries no reasoning leaves `reasoning` absent instead of seeding an empty segment.
 */
export function mergeStreamText(
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
