import { create } from "zustand";
import { persist } from "zustand/middleware";

import type { IDesktopNavigationBarStoreProperties } from "@/core/layout/models/NavigationBarModels";

function readInitialSidebarState(): boolean {
	if (typeof window === "undefined") {
		return false;
	}

	const persistedStateRaw = localStorage.getItem("desktop-navigation-bar-storage");

	if (persistedStateRaw) {
		try {
			const persistedState = JSON.parse(persistedStateRaw) as { state?: { sidebarState?: unknown } };
			const sidebarState = persistedState.state?.sidebarState;

			if (typeof sidebarState === "boolean") {
				return sidebarState;
			}
		} catch {
			// Ignore malformed persisted state and use default.
		}
	}

	return false;
}

export const useDesktopNavigationBarStore = create<IDesktopNavigationBarStoreProperties>()(
	persist(
		(set) => ({
			sidebarState: readInitialSidebarState(),
			actions: {
				setSidebarState: (state) => {
					set({ sidebarState: state });
				},
			},
		}),
		{
			name: "desktop-navigation-bar-storage",
			partialize: (state) => ({ sidebarState: state.sidebarState }),
			merge: (persistedState, currentState) => {
				const persistedRecord =
					typeof persistedState === "object" && persistedState !== null ? (persistedState as Record<string, unknown>) : {};
				const persistedSidebarState = persistedRecord["sidebarState"];

				return {
					...currentState,
					sidebarState: typeof persistedSidebarState === "boolean" ? persistedSidebarState : currentState.sidebarState,
				};
			},
		},
	),
);
