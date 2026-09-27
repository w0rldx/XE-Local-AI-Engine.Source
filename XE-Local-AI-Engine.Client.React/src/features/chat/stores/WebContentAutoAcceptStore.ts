import { create } from "zustand";

/* eslint-disable react-doctor/auth-token-in-web-storage -- xe-node-chat-web-auto:* holds a per-conversation UI toggle only; no credential or session token is persisted here. */

// Per-conversation web content review mode (ADR 0017, D8). Off = every web_fetch/web_search result is shown for review
// before the model reads it; on = it reaches the model directly. Every conversation starts off, so only "true" is stored.
const storageKeyPrefix = "xe-node-chat-web-auto:";

function readStored(conversationId: string): boolean {
	try {
		return globalThis.localStorage?.getItem(`${storageKeyPrefix}${conversationId}`) === "true";
	} catch {
		return false;
	}
}

function writeStored(conversationId: string, value: boolean): void {
	try {
		if (value) {
			globalThis.localStorage?.setItem(`${storageKeyPrefix}${conversationId}`, "true");
		} else {
			globalThis.localStorage?.removeItem(`${storageKeyPrefix}${conversationId}`);
		}
	} catch {
		// Unavailable storage or quota: the in-memory choice still holds for this page's lifetime.
	}
}

interface WebContentAutoAcceptStore {
	// Choices made in this page's lifetime; anything absent falls back to storage.
	byConversation: Record<string, boolean>;
	actions: {
		setAutoAccept: (conversationId: string, value: boolean) => void;
	};
}

const useWebContentAutoAcceptStore = create<WebContentAutoAcceptStore>()((set) => ({
	byConversation: {},
	actions: {
		setAutoAccept: (conversationId, value) => {
			writeStored(conversationId, value);
			set((state) => ({ byConversation: { ...state.byConversation, [conversationId]: value } }));
		},
	},
}));

/** Read at send time: the review mode the turn for `conversationId` carries. A conversation without an id is off. */
export function isWebContentAutoAccepted(conversationId: string): boolean {
	if (!conversationId) {
		return false;
	}

	return useWebContentAutoAcceptStore.getState().byConversation[conversationId] ?? readStored(conversationId);
}

/** Drops a deleted conversation's choice, so its storage key does not outlive it. */
export function forgetWebContentAutoAccept(conversationId: string): void {
	writeStored(conversationId, false);
	useWebContentAutoAcceptStore.setState((state) => {
		const { [conversationId]: _forgotten, ...rest } = state.byConversation;
		return { byConversation: rest };
	});
}

export function useWebContentAutoAccept(conversationId: string): boolean {
	const inMemory = useWebContentAutoAcceptStore((state) => (conversationId ? state.byConversation[conversationId] : false));
	return inMemory ?? readStored(conversationId);
}

export function useSetWebContentAutoAccept(): (conversationId: string, value: boolean) => void {
	return useWebContentAutoAcceptStore((state) => state.actions.setAutoAccept);
}
