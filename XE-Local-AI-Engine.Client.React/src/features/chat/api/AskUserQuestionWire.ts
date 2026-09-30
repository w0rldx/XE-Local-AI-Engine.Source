import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";

/**
 * THE single place the `ask_user` stream-event contract is read: the stream-event field names and the question JSON
 * shape the model emits. Nothing else in the client reads those field names — a backend rename is a one-line fix
 * here, not a sweep. The answers are posted through the generated `resolveUserQuestionMutation`.
 */

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
			? (parsed as { questions: unknown[] }).questions
			: undefined;
	if (!rawQuestions) {
		return undefined;
	}

	const questions = rawQuestions.map(toQuestion).filter((question): question is UserQuestion => question !== undefined);
	return questions.length > 0 ? { requestId, questions } : undefined;
}
