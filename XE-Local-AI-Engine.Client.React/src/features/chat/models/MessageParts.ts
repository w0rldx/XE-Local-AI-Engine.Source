import type { PendingUserQuestion } from "@/features/chat/api/AskUserQuestionWire";
import type {
	ChatMessagePart,
	ChatNoticePart,
	ChatReasoningPart,
	ChatTextPart,
	ChatToolPart,
	ToolCallState,
} from "@/features/chat/models/ChatModels";

/**
 * Single source of truth for the ordered interleave (`reasoning → tool → reasoning → …`). Both the streaming
 * reducer and `mapMessage` (reload) feed their accumulated segments + tool entries through this pure builder, so
 * the live render and the post-reload render are byte-identical.
 */

/** An ordered reasoning run. `sequence` is the stream `sequence` at which the segment opened (the ordering key). */
export interface ReasoningSegmentInput {
	id: string;
	sequence: number;
	text: string;
}

/** A tool call collapsed to its latest known state, keyed by tool-call id. `sequence` is when it was first seen. */
export interface ToolEntryInput {
	id: string;
	sequence: number;
	name: string;
	state: ToolCallState;
	args?: string;
	result?: string;
	requiresApproval?: boolean;
	// Set while the tool waits on the operator's approval decision; the approval request id the resolve
	// endpoint keys on. Cleared once the tool completes/rejects.
	pendingApprovalRequestId?: string;
	// The backend's per-request answer to "can a session-scoped approval be remembered for this call?". Undefined when
	// the backend did not resolve it; cleared with the prompt.
	pendingApprovalSessionScopeEligible?: boolean;
	// Set while an `ask_user` call waits on the operator's answer; the question payload the inline card renders and
	// posts back. Cleared on the same terms as the approval prompt above.
	pendingQuestion?: PendingUserQuestion;
}

/** A mid-turn answer/narration run (rare for local models; here for forward-compat round-trips). */
export interface TextSegmentInput {
	id: string;
	sequence: number;
	text: string;
}

/** A single fire-and-forget non-fatal turn notice (model substitution, tool disabled, history truncated). */
export interface NoticeEntryInput {
	id: string;
	sequence: number;
	noticeKind: string;
	text: string;
	detail?: string;
}

function toReasoningPart(segment: ReasoningSegmentInput): ChatReasoningPart {
	return { kind: "reasoning", id: segment.id, sequence: segment.sequence, text: segment.text };
}

function toToolPart(entry: ToolEntryInput): ChatToolPart {
	return {
		kind: "tool",
		id: entry.id,
		sequence: entry.sequence,
		name: entry.name,
		state: entry.state,
		args: entry.args,
		result: entry.result,
		requiresApproval: entry.requiresApproval,
		pendingApprovalRequestId: entry.pendingApprovalRequestId,
		pendingApprovalSessionScopeEligible: entry.pendingApprovalSessionScopeEligible,
		pendingQuestion: entry.pendingQuestion,
	};
}

function toTextPart(segment: TextSegmentInput): ChatTextPart {
	return { kind: "text", id: segment.id, sequence: segment.sequence, text: segment.text };
}

function toNoticePart(entry: NoticeEntryInput): ChatNoticePart {
	return { kind: "notice", id: entry.id, sequence: entry.sequence, noticeKind: entry.noticeKind, text: entry.text, detail: entry.detail };
}

/**
 * Merges ordered reasoning segments, tool entries, (optional) text segments and (optional) notice entries into one
 * `ChatMessagePart[]` sorted by wire `sequence`. Empty reasoning/text segments are dropped so a freshly
 * opened-but-not-yet-filled segment never renders an empty Thoughts block. Ties on `sequence` keep input order
 * (reasoning, then tool, then notice, then text) so an unlikely collision stays deterministic.
 */
export function buildMessageParts(
	reasoningSegments: readonly ReasoningSegmentInput[],
	toolEntries: readonly ToolEntryInput[],
	textSegments: readonly TextSegmentInput[] = [],
	noticeEntries: readonly NoticeEntryInput[] = [],
): ChatMessagePart[] {
	const parts: ChatMessagePart[] = [];

	for (const segment of reasoningSegments) {
		if (segment.text.trim().length > 0) {
			parts.push(toReasoningPart(segment));
		}
	}

	for (const entry of toolEntries) {
		parts.push(toToolPart(entry));
	}

	for (const entry of noticeEntries) {
		parts.push(toNoticePart(entry));
	}

	for (const segment of textSegments) {
		if (segment.text.trim().length > 0) {
			parts.push(toTextPart(segment));
		}
	}

	// Stable sort by sequence: Array.prototype.sort is stable in every supported engine, so equal sequences keep
	// the push order above (reasoning < tool < notice < text).
	return parts.sort((left, right) => left.sequence - right.sequence);
}
