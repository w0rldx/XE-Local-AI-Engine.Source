import { create } from "zustand";

// The conversation `/chat` should open, handed over across a navigation: the "Open conversation" action on a row of
// the agent-runs page, a recent chat on Home, or Home's "New chat", which asks for a fresh conversation instead.
//
// It lives in core/ rather than in the chat feature for the same reason PendingComposerTextStore does: two features
// touch it (another feature's page writes it, the chat page reads it), which is exactly the "shared code belongs in
// core/" case the no-cross-feature dependency rule names. Writing chat's own NodeChatPreferencesStore straight from
// the other feature's page would be the shorter diff and a new tracked cross-feature edge.
//
// Deliberately NOT persisted: this is one navigation's intent, not a preference. Chat consumes it on mount and writes
// the id into the persisted selection it already owns, which is what makes the choice survive a reload.
interface PendingChatConversationStore {
	pendingConversationId: string;
	newConversationRequested: boolean;
	actions: {
		setPendingConversationId: (conversationId: string) => void;
		/** Asks `/chat` to start a new conversation on its next mount, the same way its own "New chat" button does. */
		requestNewConversation: () => void;
		/** Returns the pending id and empties the store, so a remount cannot re-select it over a later choice. */
		consume: () => string;
		/** Returns whether a new conversation was requested and clears the request, for the same reason. */
		consumeNewConversationRequest: () => boolean;
	};
}

export const usePendingChatConversationStore = create<PendingChatConversationStore>()((set, get) => ({
	pendingConversationId: "",
	newConversationRequested: false,
	actions: {
		setPendingConversationId: (conversationId) => set({ pendingConversationId: conversationId, newConversationRequested: false }),
		requestNewConversation: () => set({ pendingConversationId: "", newConversationRequested: true }),
		consume: () => {
			const { pendingConversationId } = get();
			if (pendingConversationId.length > 0) {
				set({ pendingConversationId: "" });
			}
			return pendingConversationId;
		},
		consumeNewConversationRequest: () => {
			const { newConversationRequested } = get();
			if (newConversationRequested) {
				set({ newConversationRequested: false });
			}
			return newConversationRequested;
		},
	},
}));
