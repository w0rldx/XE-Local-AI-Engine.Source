import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";

/**
 * THE single place the `ask_user` wire contract lives: the stream-event field names, the question JSON shape the
 * model emits, and the resolve POST. Nothing else in the client reads those field names — a backend rename is a
 * one-line fix here, not a sweep. See Plans/2026-08-04-ask-user-question-tool-plan.md §4.3/§4.4.
 *
 * The resolve endpoint has no generated client yet (the OpenAPI regen happens at integration). {@link
 * resolveUserQuestionMutation} is deliberately shaped exactly like the generated `resolveToolApprovalMutation()` —
 * a zero-arg factory returning `{ mutationFn }` over `{ body }` — so swapping it for the generated one is a single
 * import-line change in `AskUserQuestionCard.tsx`.
 */

/** Route the browser POSTs the answers to. Mirrors `/api/local/v1/chat/approvals/resolve`. */
const RESOLVE_USER_QUESTION_URL = "/api/local/v1/chat/questions/resolve";

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

export interface ResolveUserQuestionBody {
	requestId: string;
	answers: UserQuestionAnswer[];
}

function toOption(value: unknown): UserQuestionOption | undefined {
	if (typeof value !== "object" || value === null) {
		return undefined;
	}

	const candidate = value as Partial<UserQuestionOption>;
	if (typeof candidate.label !== "string" || candidate.label.length === 0) {
		return undefined;
	}

	return {
		label: candidate.label,
		description: typeof candidate.description === "string" ? candidate.description : undefined,
		recommended: candidate.recommended === true,
	};
}

function toQuestion(value: unknown): UserQuestion | undefined {
	if (typeof value !== "object" || value === null) {
		return undefined;
	}

	const candidate = value as Partial<UserQuestion>;
	if (typeof candidate.question !== "string" || candidate.question.length === 0) {
		return undefined;
	}

	const options = Array.isArray(candidate.options)
		? candidate.options.map(toOption).filter((option): option is UserQuestionOption => option !== undefined)
		: [];
	if (options.length === 0) {
		return undefined;
	}

	return {
		// `UserQuestionSpec.Header` is a non-nullable C# string, so an omitted header rides the wire as "" rather than
		// null — treat blank as absent, otherwise the card renders an empty legend.
		header: typeof candidate.header === "string" && candidate.header.trim().length > 0 ? candidate.header : undefined,
		question: candidate.question,
		multiSelect: candidate.multiSelect === true,
		options,
	};
}

/**
 * Maps a `question-requested` stream event to the pending prompt the reducer attaches to the tool part. The questions
 * ride the wire as a JSON string (the model's raw tool arguments), so this is the trust boundary: malformed JSON or a
 * question with no usable options yields `undefined` rather than a half-rendered card — the turn then simply shows the
 * plain waiting tool card until the server-side timeout returns its "not answered" result.
 */
export function parsePendingUserQuestion(event: NodeChatStreamEventDto): PendingUserQuestion | undefined {
	const requestId = event.questionRequestId;
	if (typeof requestId !== "string" || requestId.length === 0 || typeof event.questions !== "string") {
		return undefined;
	}

	let parsed: unknown;
	try {
		parsed = JSON.parse(event.questions);
	} catch {
		return undefined;
	}

	// Accept both the bare array and the `{ questions: [...] }` envelope so a backend that forwards the tool's raw
	// arguments verbatim and one that unwraps them both land here.
	const rawQuestions = Array.isArray(parsed)
		? parsed
		: typeof parsed === "object" && parsed !== null && Array.isArray((parsed as { questions?: unknown }).questions)
			? ((parsed as { questions: unknown[] }).questions)
			: undefined;
	if (!rawQuestions) {
		return undefined;
	}

	const questions = rawQuestions.map(toQuestion).filter((question): question is UserQuestion => question !== undefined);
	return questions.length > 0 ? { requestId, questions } : undefined;
}

/**
 * Posts the operator's answers to the loopback resolve endpoint, releasing the parked turn. Interim stand-in for the
 * not-yet-generated mutation; same `{ mutationFn }` shape, so the swap is one import line.
 */
export function resolveUserQuestionMutation(): {
	mutationFn: (options: { body: ResolveUserQuestionBody }) => Promise<unknown>;
} {
	return {
		mutationFn: async ({ body }) => (await axiosInstance.post(RESOLVE_USER_QUESTION_URL, body)).data,
	};
}
