import { create } from "zustand";

// Whether a job that finishes while the tab is hidden also raises an OS notification. Stored in this browser only,
// because the permission it needs is the browser's; off until the user turns it on.
const STORAGE_KEY = "xe-job-system-notifications";

interface JobNotificationStore {
	systemNotifications: boolean;
	actions: {
		setSystemNotifications: (value: boolean) => void;
	};
}

function readStored(): boolean {
	try {
		return globalThis.localStorage?.getItem(STORAGE_KEY) === "true";
	} catch {
		return false;
	}
}

export const useJobNotificationStore = create<JobNotificationStore>()((set) => ({
	systemNotifications: readStored(),
	actions: {
		setSystemNotifications: (value) => {
			try {
				globalThis.localStorage?.setItem(STORAGE_KEY, String(value));
			} catch {
				// Unavailable storage or quota: the choice still holds for this session.
			}
			set({ systemNotifications: value });
		},
	},
}));
