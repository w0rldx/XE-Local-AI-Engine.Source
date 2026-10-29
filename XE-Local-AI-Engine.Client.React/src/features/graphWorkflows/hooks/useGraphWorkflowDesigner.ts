// The Assistant tab's whole state, held by the editor mode rather than by the panel: the narrow layout renders the side
// pane inside a drawer that unmounts when it closes, and closing a drawer must neither drop the conversation nor
// orphan a turn in flight. The conversation lives in memory only; New chat and a workflow switch clear it.

import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { ApiError } from "@/core/api/errors/ApiError";
import type { GraphWorkflowEditorState } from "@/features/graphWorkflows/hooks/useGraphWorkflowEditor";
import type { GraphWorkflowDesignerMessage } from "@/features/graphWorkflows/models/GraphWorkflowDesignerModels";
import type { GraphWorkflowDesignerErrorResponse } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	useGraphWorkflowDesignerTurn,
	useGraphWorkflowLlmModelOptions,
} from "@/features/graphWorkflows/queries/useGraphWorkflows";

export type GraphWorkflowSideTab = "inspector" | "assistant";

/** The endpoint's history cap. The newest messages are kept, so the history still ends with the operator's turn. */
const DESIGNER_MAX_MESSAGES = 50;

/**
 * The endpoint's per-message cap. A model reply can be longer, and replaying it whole would have every later turn
 * refused, so an assistant message is cut to fit when it is SENT. The panel keeps showing the full reply.
 */
const DESIGNER_MAX_MESSAGE_CHARS = 8000;

function forReplay(message: GraphWorkflowDesignerMessage): GraphWorkflowDesignerMessage {
	return message.role === "assistant" && message.text.length > DESIGNER_MAX_MESSAGE_CHARS
		? { role: message.role, text: `${message.text.slice(0, DESIGNER_MAX_MESSAGE_CHARS - 1)}…` }
		: message;
}

/**
 * Why the last turn failed, in the operator's terms. A busy node (409 `NodeBusy`) is a wait, not a fault; a refused
 * cloud model (403 `CloudLocalDataWithheld`) is a grant the operator can give; unusable model output or a timeout
 * (422 `Unparseable`) or a provider that rejected the request (502 `ProviderFailed`) is worth another try or another
 * model. `reloaded` is no server answer at all: the workflow was reloaded under the turn, which was then cancelled.
 * Everything else carries the error for the panel's generic message.
 */
export type GraphWorkflowDesignerFailure =
	| { readonly kind: "busy" }
	| { readonly kind: "cloudRefused"; readonly error: unknown }
	| { readonly kind: "unparseable" }
	| { readonly kind: "providerFailed" }
	| { readonly kind: "reloaded" }
	| { readonly kind: "error"; readonly error: unknown };

function classifyFailure(error: unknown): GraphWorkflowDesignerFailure {
	// The refusals are the designer's typed `{ code, message, grant }` body, not ProblemDetails; the interceptor keeps it
	// whole on the ApiError.
	const code =
		error instanceof ApiError
			? (error.apiProblemDetails as unknown as Partial<GraphWorkflowDesignerErrorResponse>).code
			: undefined;
	return code === "NodeBusy"
		? { kind: "busy" }
		: code === "CloudLocalDataWithheld"
			? { kind: "cloudRefused", error }
			: code === "Unparseable"
				? { kind: "unparseable" }
				: code === "ProviderFailed"
					? { kind: "providerFailed" }
					: { kind: "error", error };
}

export interface UseGraphWorkflowDesignerOptions {
	readonly editor: GraphWorkflowEditorState;
	readonly definitionId: string | undefined;
	readonly selectedNodeKey: string | undefined;
	readonly isNarrow: boolean;
}

export function useGraphWorkflowDesigner({ editor, definitionId, selectedNodeKey, isNarrow }: UseGraphWorkflowDesignerOptions) {
	const turn = useGraphWorkflowDesignerTurn();
	const [messages, setMessages] = useState<readonly GraphWorkflowDesignerMessage[]>([]);
	const [draft, setDraft] = useState("");
	// Null until the operator picks, so the default keeps following the list as it loads.
	const [pickedModel, setPickedModel] = useState<string | null>(null);
	const [tab, setTab] = useState<GraphWorkflowSideTab>("inspector");
	// Narrow layout only: whether the drawer is open for the side pane even with nothing selected.
	const [paneOpen, setPaneOpen] = useState(false);
	// Asked for once the tab is first shown, like the config panel's pickers: most editor visits never open it.
	const modelsQuery = useGraphWorkflowLlmModelOptions({ enabled: definitionId !== undefined && tab === "assistant" });
	const [failure, setFailure] = useState<GraphWorkflowDesignerFailure | undefined>(undefined);
	// Replies received and replies the operator has had on screen. Counters rather than a flag, so the dot is derived:
	// a reply that lands while the tab is showing is seen on that same render.
	const [replies, setReplies] = useState(0);
	const [seenReplies, setSeenReplies] = useState(0);
	// The turn in flight. A settled request acts only while it is still this one, so a turn cancelled by a workflow
	// switch can never end or fill the next one.
	const requestRef = useRef<{ readonly controller: AbortController; readonly message: GraphWorkflowDesignerMessage } | undefined>(
		undefined,
	);

	const modelOptions = useMemo(() => modelsQuery.data ?? [], [modelsQuery.data]);
	const defaultModel = modelOptions.find((option) => option.cloudProvider === undefined) ?? modelOptions[0];
	const modelName = pickedModel ?? defaultModel?.value ?? "";

	const visible = tab === "assistant" && (!isNarrow || paneOpen);
	if (visible && seenReplies !== replies) {
		setSeenReplies(replies);
	}
	const unread = !visible && seenReplies !== replies;

	// Another workflow, or leaving the editor: the conversation was about the old graph.
	const { endGenerating, rejectProposal } = editor;
	// biome-ignore lint/correctness/useExhaustiveDependencies: the reset is keyed on the workflow, not on a value it reads.
	useEffect(
		() => () => {
			requestRef.current?.controller.abort();
			requestRef.current = undefined;
			endGenerating();
			rejectProposal();
			setMessages([]);
			setFailure(undefined);
		},
		[definitionId, endGenerating, rejectProposal],
	);

	/** A turn that did not happen: its message leaves the history and goes back into the box, as a retry needs it. */
	const withdraw = (message: GraphWorkflowDesignerMessage): void => {
		setMessages((current) => current.filter((entry) => entry !== message));
		setDraft((current) => (current.length === 0 ? message.text : current));
	};

	// The editor left `generating` while a turn was still ours: a definition load reset it under us (a save elsewhere
	// and Reload, or a version bump). The answer would describe a graph that is gone, so the turn is cancelled, and said.
	// biome-ignore lint/correctness/useExhaustiveDependencies: keyed on the mode transition; the ref and setters are stable.
	useEffect(() => {
		const pending = requestRef.current;
		if (editor.mode === "generating" || pending === undefined) {
			return;
		}
		requestRef.current = undefined;
		pending.controller.abort();
		withdraw(pending.message);
		setFailure({ kind: "reloaded" });
	}, [editor.mode]);

	const run = (history: readonly GraphWorkflowDesignerMessage[], text: string): void => {
		if (definitionId === undefined || modelName.length === 0) {
			return;
		}
		const basis = editor.graph;
		if (!editor.beginGenerating()) {
			return;
		}
		const userMessage: GraphWorkflowDesignerMessage = { role: "user", text };
		const conversation = [...history, userMessage];
		setMessages(conversation);
		setDraft("");
		setFailure(undefined);
		const controller = new AbortController();
		requestRef.current = { controller, message: userMessage };
		turn
			.mutateAsync({
				body: {
					definitionId,
					modelName,
					messages: conversation.slice(-DESIGNER_MAX_MESSAGES).map(forReplay),
					graph: basis,
					selectedNodeKey: selectedNodeKey ?? null,
					intent: "auto",
				},
				signal: controller.signal,
			})
			.then((response) => {
				if (requestRef.current?.controller !== controller) {
					return;
				}
				requestRef.current = undefined;
				// Never an empty assistant turn in the history the next request sends; the server guarantees a reply, this
				// holds even if it ever does not.
				if (response.reply.trim().length > 0) {
					setMessages((current) => [...current, { role: "assistant", text: response.reply }]);
				}
				setReplies((count) => count + 1);
				if (response.proposal) {
					editor.showProposal({
						proposal: response.proposal,
						changes: response.changes,
						valid: response.validation?.valid ?? false,
						errors: (response.validation?.errors ?? []).map((error) =>
							error.key ? `${error.key}: ${error.message}` : error.message,
						),
						basis,
					});
				} else {
					editor.endGenerating();
				}
			})
			.catch((error: unknown) => {
				if (requestRef.current?.controller !== controller) {
					return;
				}
				requestRef.current = undefined;
				editor.endGenerating();
				// The history the next turn sends never holds a question without its answer.
				withdraw(userMessage);
				if (!controller.signal.aborted) {
					setFailure(classifyFailure(error));
				}
			});
	};

	const send = (): void => {
		const text = draft.trim();
		if (text.length > 0) {
			run(messages, text);
		}
	};

	/** The stale case: the proposal answered another graph, so the last question is asked again about this one. */
	const regenerate = (): void => {
		const index = messages.findLastIndex((message) => message.role === "user");
		const last = messages[index];
		if (last === undefined) {
			return;
		}
		editor.rejectProposal();
		run(messages.slice(0, index), last.text);
	};

	const cancel = (): void => {
		requestRef.current?.controller.abort();
	};

	const newChat = (): void => {
		setMessages([]);
		setFailure(undefined);
	};

	// Turning to the Assistant also counts as opening the narrow drawer for it: the drawer may be open for a selection
	// instead, and a reply read there is a reply seen.
	const selectTab = useCallback((next: GraphWorkflowSideTab): void => {
		setTab(next);
		if (next === "assistant") {
			setPaneOpen(true);
		}
	}, []);

	const openAssistant = useCallback((prefill?: string): void => {
		setTab("assistant");
		setPaneOpen(true);
		if (prefill !== undefined) {
			setDraft((current) => (current.trim().length === 0 ? prefill : current));
		}
	}, []);

	return {
		messages,
		draft,
		setDraft,
		modelOptions,
		modelName,
		setModelName: setPickedModel,
		tab,
		setTab: selectTab,
		paneOpen,
		closePane: () => setPaneOpen(false),
		unread,
		failure,
		isGenerating: editor.mode === "generating",
		send,
		regenerate,
		cancel,
		newChat,
		openAssistant,
	};
}

export type GraphWorkflowDesignerState = ReturnType<typeof useGraphWorkflowDesigner>;
