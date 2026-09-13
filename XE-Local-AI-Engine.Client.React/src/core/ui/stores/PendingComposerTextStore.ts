import { create } from "zustand";

// Text handed to the chat composer across a navigation — today, a transcript sent from a transcription session.
//
// It lives in core/ rather than in the chat feature because two features touch it: the chat composer reads it and
// another feature's page writes it, which is exactly the "shared code belongs in core/" case the no-cross-feature
// dependency rule names.
//
// Deliberately NOT persisted: a transcript can be tens of kilobytes of whatever the microphone heard, and it has no
// business in localStorage. That also rules out the two navigation-shaped alternatives — a search param would put it
// in the URL and the browser history in plaintext, and TanStack Router's history `state` does not survive a reload,
// so a refresh would silently drop it.
//
// This carries text ACROSS a navigation. In-page composer insertions (dictation) append through a callback on the
// composer itself and must not route through here: a second writer would race the consuming effect below.
interface PendingComposerTextStore {
	pendingText: string;
	actions: {
		setPendingText: (text: string) => void;
		/** Returns the pending text and empties the store, so a remount cannot insert it twice. */
		consume: () => string;
	};
}

export const usePendingComposerTextStore = create<PendingComposerTextStore>()((set, get) => ({
	pendingText: "",
	actions: {
		setPendingText: (text) => set({ pendingText: text }),
		consume: () => {
			const { pendingText } = get();
			if (pendingText.length > 0) {
				set({ pendingText: "" });
			}
			return pendingText;
		},
	},
}));
