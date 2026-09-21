import { create } from "zustand";

// The conversation `/chat` should open, handed over across a navigation — today, the "Open conversation" action on a
// row of the agent-runs page.
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
	actions: {
		setPendingConversationId: (conversationId: string) => void;
		/** Returns the pending id and empties the store, so a remount cannot re-select it over a later choice. */
		consume: () => string;
	};
}

export const usePendingChatConversationStore = create<PendingChatConversationStore>()((set, get) => ({
	pendingConversationId: "",
	actions: {
		setPendingConversationId: (conversationId) => set({ pendingConversationId: conversationId }),
		consume: () => {
			const { pendingConversationId } = get();
			if (pendingConversationId.length > 0) {
				set({ pendingConversationId: "" });
			}
			return pendingConversationId;
		},
	},
}));
