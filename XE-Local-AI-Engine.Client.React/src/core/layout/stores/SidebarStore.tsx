import { create } from "zustand";

import type { SidebarState } from "@/core/layout/models/SidebarModels";

export const useSidebarStore = create<SidebarState>()((set) => ({
	collapsed: false,
	width: "270px",
	collapsedWidth: "80px",
	isAnimating: false,
	transitionDuration: 300,
	syncSidebarConfig: (config) => {
		set((state) => ({
			...state,
			...config,
		}));
	},
	startSidebarAnimation: () => {
		set({ isAnimating: true });
	},
	finishSidebarAnimation: () => {
		set({ isAnimating: false });
	},
}));
