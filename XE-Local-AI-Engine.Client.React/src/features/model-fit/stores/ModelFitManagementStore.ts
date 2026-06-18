import { create } from "zustand";

import { defaultModelFitUseCase, type ModelFitUseCase } from "@/features/model-fit/models/ModelFitModels";

// Transient UI state for the model-fit advisor page. Server state (snapshots, hardware profile, running models,
// browse results, token status) lives in TanStack Query — this store holds ONLY ephemeral UI/form state that must
// survive a remount within a session and is never derived from the server: the selected use case the
// latest-recommendations query is keyed on, the committed GGUF browse search term (the term the browse query keys
// on, distinct from the raw input box value the page owns locally), and the masked HF token input draft.
interface ModelFitManagementStore {
	useCase: ModelFitUseCase;
	// The committed browse query (the value the browse TanStack query is keyed on). Empty until the operator submits a
	// search, which keeps the browse query disabled until then.
	browseQuery: string;
	// Draft of the masked HF token input. Held here so the value is never derived from / written back to server state
	// (the token is write-only). Cleared on a successful submit.
	tokenDraft: string;
	actions: {
		setUseCase: (useCase: ModelFitUseCase) => void;
		setBrowseQuery: (browseQuery: string) => void;
		setTokenDraft: (tokenDraft: string) => void;
		clearTokenDraft: () => void;
	};
}

export const useModelFitManagementStore = create<ModelFitManagementStore>()((set) => ({
	useCase: defaultModelFitUseCase,
	browseQuery: "",
	tokenDraft: "",
	actions: {
		setUseCase: (useCase) => set({ useCase }),
		setBrowseQuery: (browseQuery) => set({ browseQuery }),
		setTokenDraft: (tokenDraft) => set({ tokenDraft }),
		clearTokenDraft: () => set({ tokenDraft: "" }),
	},
}));
