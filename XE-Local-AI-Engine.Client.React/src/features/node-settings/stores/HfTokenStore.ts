import { create } from "zustand";

// Transient UI state for the Hugging Face token card on the Node Settings page (relocated from the model-fit
// advisor). Holds ONLY the masked HF token input draft — kept here so the value is never derived from / written
// back to server state (the token is write-only). Cleared on a successful submit.
interface HfTokenStore {
	tokenDraft: string;
	actions: {
		setTokenDraft: (tokenDraft: string) => void;
		clearTokenDraft: () => void;
	};
}

export const useHfTokenStore = create<HfTokenStore>()((set) => ({
	tokenDraft: "",
	actions: {
		setTokenDraft: (tokenDraft) => set({ tokenDraft }),
		clearTokenDraft: () => set({ tokenDraft: "" }),
	},
}));
