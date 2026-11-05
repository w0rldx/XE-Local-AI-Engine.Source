// The ordered parts of an assistant message, shared by the chat stream fold, the part renderer and every feature that
// shows a transcript. The members that ride the SignalR stream have no OpenAPI equivalent, so they stay hand-authored.

/** One selectable option. `recommended` is advisory only — the card badges it but never pre-selects it. */
export interface UserQuestionOption {
	label: string;
	description?: string;
	recommended?: boolean;
}

/** One question of the 1–4 the model may ask in a single `ask_user` call. */
export interface UserQuestion {
	header?: string;
	question: string;
	multiSelect?: boolean;
	options: UserQuestionOption[];
}

/**
 * A live, unanswered `ask_user` prompt riding the matching tool part. Transient (live-only): cleared once the tool
 * call completes/fails, and never present on a reloaded/persisted turn — exactly like `pendingApprovalRequestId`.
 */
export interface PendingUserQuestion {
	requestId: string;
	questions: UserQuestion[];
}

/** One answered question. `other` carries the free-text row the client always offers (never model-declared). */
export interface UserQuestionAnswer {
	question: string;
	selected: string[];
	other?: string;
}

export interface NodeChatWebSearchResultDto {
	title: string;
	url: string;
	snippet: string;
}

// Two stages share this shape. `request` is the pre-send consent: `url` (web_fetch) or `query` (web_search) is exactly what
// will be sent, nothing has left the node yet. `result` is the review of what came back: `web_fetch` fills the page
// fields, `web_search` fills `backend` and `results`. A missing `stage` (an older node's replay) is a result review.
// All but `toolName`, `stage`, `url` and `query` are server-controlled text.
export interface NodeChatWebReviewPreviewDto {
	toolName: string;
	stage?: "request" | "result" | null;
	url?: string | null;
	query?: string | null;
	finalUrl?: string | null;
	title?: string | null;
	contentType?: string | null;
	truncated?: boolean | null;
	text?: string | null;
	backend?: string | null;
	results?: NodeChatWebSearchResultDto[] | null;
}

export type ToolCallState = "requesting" | "waiting" | "received" | "failed" | "denied";

export type ChatMessagePartKind = "reasoning" | "tool" | "text" | "notice";

/** A folded "Thoughts" run. Each reasoning segment renders as its own block, so reasoning and tools interleave in stream order. */
export interface ChatReasoningPart {
	kind: "reasoning";
	// Stable per segment (`${messageId}:${sequence}` live, `${messageId}:${sequence}` on reload).
	id: string;
	// Wire ordering key (the monotonic stream `sequence` at which this segment opened).
	sequence: number;
	text: string;
}

/** A single tool call, collapsed requested→completed by tool-call id, rendered as one state-driven card. */
export interface ChatToolPart {
	kind: "tool";
	// Tool-call id (stable across requested/completed so the card never duplicates).
	id: string;
	sequence: number;
	name: string;
	state: ToolCallState;
	args?: string;
	result?: string;
	requiresApproval?: boolean;
	// When set, the tool is paused awaiting the operator's decision; this is the approval request id the
	// Approve/Deny controls post to the loopback resolve endpoint. Transient, live-only: cleared once the tool
	// completes (approved) or is rejected, and never present on a reloaded/persisted turn.
	pendingApprovalRequestId?: string;
	// The backend's per-request answer to whether an "approve for this session" decision can be remembered for this
	// exact call. Preferred over the tool catalog's tool-identity flag; undefined when the backend did not resolve it
	// (a reconnect replay), in which case the card falls back to the catalog.
	pendingApprovalSessionScopeEligible?: boolean;
	// When set, the pending approval is a web content review (`web_fetch`/`web_search`): what was retrieved, shown to
	// the operator before the model reads it. Cleared on exactly the same terms as `pendingApprovalRequestId`.
	pendingWebReview?: NodeChatWebReviewPreviewDto;
	// When set, this is an `ask_user` call parked on the operator's answer: the question payload the inline answer
	// card renders. Transient and live-only on exactly the same terms as `pendingApprovalRequestId` — cleared when
	// the tool call completes/fails or the turn terminalizes, and never present on a reloaded/persisted turn.
	pendingQuestion?: PendingUserQuestion;
	// Images the tool returned (an MCP image block), persisted as `image` parts keyed to this call. Present only on a
	// reloaded turn: the live stream carries the result text, which names each image by a placeholder.
	images?: ChatToolImage[];
	// Live only: the global streamed-content offset at which this call was requested. The text before it is interim
	// narration the reducer splits into a `text` part; never present on a reloaded turn.
	contentOffset?: number;
}

/** One image a tool call returned. `src` is always a `data:image/` URI. */
export interface ChatToolImage {
	mediaType: string;
	src: string;
}

/**
 * Interim narration the model streamed before a tool call ("Let me search for …"), closed at that call and ordered
 * just before its card. Rendered muted; the trailing answer still renders from `message.content`, which holds only
 * the text after the last tool call.
 */
export interface ChatTextPart {
	kind: "text";
	id: string;
	sequence: number;
	text: string;
}

/**
 * A non-fatal "turn notice" (model substitution, tool disabled, history truncated) surfaced by the backend as its
 * own stream event / persisted part kind. Rendered as a small muted system-style row — distinct from an error and
 * from the plain answer text — never a fatal state for the turn.
 */
export interface ChatNoticePart {
	kind: "notice";
	id: string;
	sequence: number;
	// One of "ModelSubstituted" | "ToolDisabled" | "HistoryTruncated" (server enum name, used to pick an icon).
	noticeKind: string;
	// Sanitized, user-facing sentence from the backend — displayed verbatim, never re-translated.
	text: string;
	// Optional structured detail beside the sentence: a stable machine code or short identifier naming why the notice
	// fired (e.g. the kebab-case adaptive-effort dispatch reason). Displayed verbatim, never re-translated; undefined
	// on notices that carry none.
	detail?: string;
}

export type ChatMessagePart = ChatReasoningPart | ChatToolPart | ChatTextPart | ChatNoticePart;
