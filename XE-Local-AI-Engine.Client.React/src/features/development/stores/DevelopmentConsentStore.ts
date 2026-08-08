import { create } from "zustand";

// One-time acknowledgement that the operator has read what Development Mode executes on this machine. Client-only and
// per-install (localStorage, this browser), deliberately: this is a DISCLOSURE record, not an authorization. Nothing
// on the backend consults it, and acknowledging changes nothing about what a Development run is permitted to do — so
// giving it a server-side contract would misrepresent it as a control, on top of forcing an API change for a flag
// whose only job is to make sure the notice was shown once.
//
// The key carries a version suffix. If the disclosure ever has to say something materially different — a new provider,
// a new capability — bumping it re-asks, which is the whole point of having asked in the first place.
const DEVELOPMENT_CONSENT_STORAGE_KEY = "xe-development-consent-v1";

interface DevelopmentConsentStore {
	acknowledged: boolean;
	actions: {
		acknowledge: () => void;
	};
}

function readStoredConsent(): boolean {
	try {
		return globalThis.localStorage?.getItem(DEVELOPMENT_CONSENT_STORAGE_KEY) === "true";
	} catch {
		// Storage can be unavailable (private mode, disabled cookies). Failing closed re-asks every visit, which is
		// the harmless direction: the operator sees the notice again rather than skipping it silently.
		return false;
	}
}

function writeStoredConsent(): void {
	try {
		globalThis.localStorage?.setItem(DEVELOPMENT_CONSENT_STORAGE_KEY, "true");
	} catch {
		// Ignore unavailable storage or quota errors; the in-memory state still unblocks this session.
	}
}

export const useDevelopmentConsentStore = create<DevelopmentConsentStore>()((set) => ({
	acknowledged: readStoredConsent(),
	actions: {
		acknowledge: () => {
			writeStoredConsent();
			set({ acknowledged: true });
		},
	},
}));
