import { create } from "zustand";

// Client-only developer mode flag. When enabled, experimental controls such as the chat sampling
// options panel are shown. Stored in localStorage (this browser only); not sent to the backend.
const DEVELOPER_MODE_STORAGE_KEY = "xe-developer-mode";

interface DeveloperModeStore {
	developerMode: boolean;
	actions: {
		setDeveloperMode: (value: boolean) => void;
		toggle: () => void;
	};
}

function readStoredDeveloperMode(): boolean {
	try {
		return globalThis.localStorage?.getItem(DEVELOPER_MODE_STORAGE_KEY) === "true";
	} catch {
		return false;
	}
}

function writeStoredDeveloperMode(value: boolean): void {
	try {
		globalThis.localStorage?.setItem(DEVELOPER_MODE_STORAGE_KEY, String(value));
	} catch {
		// Ignore unavailable storage or quota errors; the in-memory state still updates.
	}
}

export const useDeveloperModeStore = create<DeveloperModeStore>()((set) => ({
	developerMode: readStoredDeveloperMode(),
	actions: {
		setDeveloperMode: (value) => {
			writeStoredDeveloperMode(value);
			set({ developerMode: value });
		},
		toggle: () => {
			set((state) => {
				const next = !state.developerMode;
				writeStoredDeveloperMode(next);
				return { developerMode: next };
			});
		},
	},
}));
