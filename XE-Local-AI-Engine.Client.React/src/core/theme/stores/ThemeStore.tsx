import { create } from "zustand";
import { persist } from "zustand/middleware";

interface ThemeState {
	accentColor: string | null;
	setAccentColor: (_hex: string | null) => void;
}

// The localStorage key the ThemeProvider's colour-scheme manager is built with; the colour scheme lives there now,
// not here. Pinned explicitly because Mantine's default key has changed between minor versions.
export const colorSchemeStorageKey = "mantine-color-scheme";

function readAccentColor(value: unknown): string | null {
	return typeof value === "string" && /^#[0-9a-f]{6}$/iu.test(value) ? value.toLowerCase() : null;
}

// Before the scheme moved to Mantine this store persisted `mode`; carry an explicit light/dark choice over once,
// never over a scheme the user has already picked through Mantine. Storage can throw (private mode, quota).
function migratePersistedMode(value: unknown) {
	if (value !== "dark" && value !== "light") {
		return;
	}

	try {
		if (window.localStorage.getItem(colorSchemeStorageKey) === null) {
			window.localStorage.setItem(colorSchemeStorageKey, value);
		}
	} catch {
		// Without storage there is nothing to migrate into; Mantine falls back to the OS scheme.
	}
}

export const useThemeStore = create<ThemeState>()(
	persist(
		(set) => ({
			accentColor: null,
			setAccentColor: (hex) => {
				set({ accentColor: readAccentColor(hex) });
			},
		}),
		{
			name: "theme-storage",
			merge: (persistedState, currentState) => {
				const persistedRecord =
					typeof persistedState === "object" && persistedState !== null ? (persistedState as Record<string, unknown>) : {};

				migratePersistedMode(persistedRecord["mode"]);

				return {
					...currentState,
					accentColor: readAccentColor(persistedRecord["accentColor"]),
				};
			},
		},
	),
);
