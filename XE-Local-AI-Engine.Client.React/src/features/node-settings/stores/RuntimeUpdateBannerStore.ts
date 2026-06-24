import { create } from "zustand";
import { persist } from "zustand/middleware";

// Persisted dismiss state for the global "llama.cpp update available" banner. We store the SPECIFIC recommended tag
// the operator dismissed (not a plain boolean) so dismissing one update does not permanently silence the banner: a
// later, newer recommended tag has a different value and the banner shows again. Backed by localStorage so a dismiss
// survives a reload but stays per-tag.
interface RuntimeUpdateBannerStoreState {
	readonly dismissedTag: string | null;
	readonly dismiss: (tag: string) => void;
}

export const useRuntimeUpdateBannerStore = create<RuntimeUpdateBannerStoreState>()(
	persist(
		(set) => ({
			dismissedTag: null,
			dismiss: (tag) => set({ dismissedTag: tag }),
		}),
		{ name: "xe-llamacpp-update-banner" },
	),
);
