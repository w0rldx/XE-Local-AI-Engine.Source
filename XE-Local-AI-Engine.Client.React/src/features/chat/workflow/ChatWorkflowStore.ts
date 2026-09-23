import { create } from "zustand";

/* eslint-disable react-doctor/auth-token-in-web-storage -- The three xe-node-chat-workflow-* keys hold UI preferences only (the activity toggle, the dismissed finished run and the "No workflow" opt-out per conversation); no credential is stored. */

// UI state for chat workflow mode. Server state (definitions, bound runs, run detail) lives in TanStack Query; this
// store only holds what the viewer picked or dismissed.
//
// Of `selectedDefinitionByConversation` only the explicit "No workflow" (`""`) is persisted: after a reload the bound-run
// list is what puts a conversation back into workflow mode, so the one pick a reload would otherwise lose is the
// operator's opt-OUT of that. A remembered workflow pick for a conversation that never ran would add only staleness.
// The pre-conversation key `""` is never persisted: it is carried over to the new conversation by the first send.
const OPTED_OUT_STORAGE_KEY = "xe-node-chat-workflow-opted-out";
const SHOW_ACTIVITY_STORAGE_KEY = "xe-node-chat-workflow-show-activity";
const DISMISSED_RUNS_STORAGE_KEY = "xe-node-chat-workflow-dismissed-runs";

function readStorage(key: string): string | null {
	try {
		return globalThis.localStorage?.getItem(key) ?? null;
	} catch {
		return null;
	}
}

function writeStorage(key: string, value: string): void {
	try {
		globalThis.localStorage?.setItem(key, value);
	} catch {
		// Private mode, a full quota or blocked storage: the preference simply does not survive the reload.
	}
}

function readStringMap(key: string): Record<string, string> {
	try {
		const parsed: unknown = JSON.parse(readStorage(key) ?? "{}");
		if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
			return {};
		}
		return Object.fromEntries(
			Object.entries(parsed as Record<string, unknown>).filter(
				(entry): entry is [string, string] => typeof entry[1] === "string",
			),
		);
	} catch {
		return {};
	}
}

/** The new selection map, with its opt-outs (and nothing else) written through to storage. */
function withSelection(selectedDefinitionByConversation: Record<string, string>) {
	const optedOut = Object.fromEntries(
		Object.entries(selectedDefinitionByConversation).filter(
			([conversationId, definitionId]) => conversationId !== "" && definitionId === "",
		),
	);
	writeStorage(OPTED_OUT_STORAGE_KEY, JSON.stringify(optedOut));
	return { selectedDefinitionByConversation };
}

interface ChatWorkflowState {
	/** Conversation id → selected Chat definition id; `""` is an explicit "no workflow". Absent = follow the bound runs. */
	readonly selectedDefinitionByConversation: Readonly<Record<string, string>>;
	/** Conversation id → the finished run whose status card the viewer dismissed. One per conversation, so it stays small. */
	readonly dismissedRunByConversation: Readonly<Record<string, string>>;
	/** "Show workflow activity" — per viewer, default on. */
	readonly showActivity: boolean;
	readonly actions: {
		selectDefinition(conversationId: string, definitionId: string): void;
		/** Moves the pick made before any conversation existed (key `""`) onto the conversation the first send created. */
		carryOverPendingPick(conversationId: string): void;
		dismissRun(conversationId: string, runId: string): void;
		setShowActivity(show: boolean): void;
	};
}

export const useChatWorkflowStore = create<ChatWorkflowState>((set) => ({
	selectedDefinitionByConversation: readStringMap(OPTED_OUT_STORAGE_KEY),
	dismissedRunByConversation: readStringMap(DISMISSED_RUNS_STORAGE_KEY),
	showActivity: readStorage(SHOW_ACTIVITY_STORAGE_KEY) !== "false",
	actions: {
		selectDefinition: (conversationId, definitionId) =>
			set((state) => withSelection({ ...state.selectedDefinitionByConversation, [conversationId]: definitionId })),
		carryOverPendingPick: (conversationId) =>
			set((state) => {
				const { "": pending, ...rest } = state.selectedDefinitionByConversation;
				return withSelection(pending === undefined ? rest : { ...rest, [conversationId]: pending });
			}),
		dismissRun: (conversationId, runId) =>
			set((state) => {
				const dismissedRunByConversation = { ...state.dismissedRunByConversation, [conversationId]: runId };
				writeStorage(DISMISSED_RUNS_STORAGE_KEY, JSON.stringify(dismissedRunByConversation));
				return { dismissedRunByConversation };
			}),
		setShowActivity: (showActivity) => {
			writeStorage(SHOW_ACTIVITY_STORAGE_KEY, String(showActivity));
			set({ showActivity });
		},
	},
}));
