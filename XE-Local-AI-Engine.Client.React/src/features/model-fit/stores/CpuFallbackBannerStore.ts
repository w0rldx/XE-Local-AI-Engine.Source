import { create } from "zustand";
import { persist } from "zustand/middleware";

// Persisted dismiss state for the global "no supported GPU — running on CPU" banner. A plain boolean is correct here
// (unlike the per-tag update banner): the notice reflects a hardware fact that does not version, so once the operator
// acknowledges it we keep it dismissed across reloads. If a supported GPU later appears, `gpuAccelAvailable` flips true
// and the banner stops rendering regardless of this flag. Backed by localStorage so the dismiss survives a reload.
interface CpuFallbackBannerStoreState {
	readonly dismissed: boolean;
	readonly dismiss: () => void;
}

export const useCpuFallbackBannerStore = create<CpuFallbackBannerStoreState>()(
	persist(
		(set) => ({
			dismissed: false,
			dismiss: () => set({ dismissed: true }),
		}),
		{ name: "xe-cpu-fallback-banner" },
	),
);
