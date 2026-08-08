import { create } from "zustand";

// Transient UI state for the GGUF browse + download flow on the Model Management page. Server state (browse results,
// inspected quants) lives in TanStack Query — this store holds the committed GGUF browse search term and the set of
// downloads started this session. Both are kept in a store (rather than page-local state) so they survive a remount
// within a session and — crucially for the in-flight set — so a download STARTED elsewhere (the advisor's
// recommendation row hands off here) becomes visible + cancellable on the Model Management download-progress panel.
interface GgufBrowseStore {
	// The committed browse query (the value the browse TanStack query is keyed on). Empty until the operator submits a
	// search, which keeps the browse query disabled until then.
	browseQuery: string;
	// Model names of GGUF downloads started this session. The backend exposes no byte-level progress, so the page
	// surfaces each as an indeterminate "downloading" row (with cancel) from this set. Shared so a download kicked off
	// from the advisor's recommendation row also appears here.
	inFlightDownloads: string[];
	actions: {
		setBrowseQuery: (browseQuery: string) => void;
		// Marks a model as in-flight (deduped).
		markInFlight: (modelName: string) => void;
		removeInFlight: (modelName: string) => void;
	};
}

export const useGgufBrowseStore = create<GgufBrowseStore>()((set) => ({
	browseQuery: "",
	inFlightDownloads: [],
	actions: {
		setBrowseQuery: (browseQuery) => set({ browseQuery }),
		markInFlight: (modelName) =>
			set((state) =>
				state.inFlightDownloads.includes(modelName) ? state : { inFlightDownloads: [...state.inFlightDownloads, modelName] },
			),
		removeInFlight: (modelName) =>
			set((state) => ({ inFlightDownloads: state.inFlightDownloads.filter((name) => name !== modelName) })),
	},
}));
