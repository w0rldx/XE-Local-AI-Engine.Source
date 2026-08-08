import { create } from "zustand";

import { defaultModelFitUseCase, type ModelFitUseCase } from "@/features/model-fit/models/ModelFitModels";

// Transient UI state for the model-fit advisor page. Server state (snapshots, hardware profile) lives in TanStack
// Query — this store holds ONLY ephemeral UI state that must survive a remount within a session and is never derived
// from the server: the selected use case the latest-recommendations query is keyed on. (The GGUF browse term and HF
// token draft moved with their panels to the Model Management / Node Settings features.)
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
