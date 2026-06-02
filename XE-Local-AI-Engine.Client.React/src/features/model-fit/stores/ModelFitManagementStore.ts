import { create } from "zustand";

import { defaultModelFitUseCase, type ModelFitUseCase } from "@/features/model-fit/models/ModelFitModels";

// Transient UI state for the model-recommendations page: the currently selected use case the latest-recommendations
// query is keyed on. Server state (the snapshots / approved images themselves) lives in TanStack Query — this store
// holds only the ephemeral selector value so it survives a remount within a session.
interface ModelFitManagementStore {
	useCase: ModelFitUseCase;
	actions: {
		setUseCase: (useCase: ModelFitUseCase) => void;
	};
}

export const useModelFitManagementStore = create<ModelFitManagementStore>()((set) => ({
	useCase: defaultModelFitUseCase,
	actions: {
		setUseCase: (useCase) => set({ useCase }),
	},
}));
