import { create } from "zustand";
import { persist } from "zustand/middleware";

import type { IDesktopNavigationBarStoreProperties } from "@/core/layout/models/NavigationBarModels";

const STORAGE_KEY = "desktop-navigation-bar-storage";

interface PersistedShape {
	sidebarState?: unknown;
	openGroups?: unknown;
}

// The persist middleware hydrates asynchronously after the first render, which would flash the default state
// for a frame. Reading the same localStorage payload synchronously here seeds the initial state so the rail
// (and the expanded groups) render in their persisted state on the very first paint — no flash, no jump.
function readPersistedState(): PersistedShape {
	if (typeof window === "undefined") {
		return {};
	}

	const persistedStateRaw = localStorage.getItem(STORAGE_KEY);

	if (!persistedStateRaw) {
		return {};
	}

	try {
		const parsed = JSON.parse(persistedStateRaw) as { state?: PersistedShape };
		return parsed.state ?? {};
	} catch {
		// Ignore malformed persisted state and use defaults.
		return {};
	}
}

function readInitialSidebarState(persisted: PersistedShape): boolean {
	return typeof persisted.sidebarState === "boolean" ? persisted.sidebarState : false;
}

function readInitialOpenGroups(persisted: PersistedShape): Record<string, boolean> {
	if (typeof persisted.openGroups !== "object" || persisted.openGroups === null) {
		return {};
	}

	return Object.fromEntries(
		Object.entries(persisted.openGroups as Record<string, unknown>).filter(([, value]) => typeof value === "boolean"),
	) as Record<string, boolean>;
}

export const useDesktopNavigationBarStore = create<IDesktopNavigationBarStoreProperties>()(
	persist(
		(set) => {
			const persisted = readPersistedState();

			return {
				sidebarState: readInitialSidebarState(persisted),
				openGroups: readInitialOpenGroups(persisted),
				actions: {
					setSidebarState: (state) => {
						set({ sidebarState: state });
					},
					setGroupOpen: (groupId, open) => {
						set((current) => ({ openGroups: { ...current.openGroups, [groupId]: open } }));
					},
				},
			};
		},
		{
			name: STORAGE_KEY,
			partialize: (state) => ({ sidebarState: state.sidebarState, openGroups: state.openGroups }),
			merge: (persistedState, currentState) => {
				const persisted: PersistedShape =
					typeof persistedState === "object" && persistedState !== null ? (persistedState as PersistedShape) : {};

				return {
					...currentState,
					sidebarState:
						typeof persisted.sidebarState === "boolean" ? persisted.sidebarState : currentState.sidebarState,
					openGroups: readInitialOpenGroups(persisted),
				};
			},
		},
	),
);
