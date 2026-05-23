import { create } from "zustand";
import { persist } from "zustand/middleware";

import {
	normalizeThemeConfiguration,
	sourceThemeConfiguration,
	type ThemeConfiguration,
	type ThemeMode,
} from "@/core/theme/config/ThemeConfiguration";
import type { ThemeState } from "@/core/theme/models/ThemeModels";

function copyThemeConfiguration(configuration: ThemeConfiguration): ThemeConfiguration {
	return JSON.parse(JSON.stringify(configuration)) as ThemeConfiguration;
}

function readPersistedMode(value: unknown, fallback: ThemeMode): ThemeMode {
	if (value === "dark" || value === "light") {
		return value;
	}

	return fallback;
}

export const useThemeStore = create<ThemeState>()(
	persist(
		(set) => ({
			mode: sourceThemeConfiguration.palette.mode,
			themeConfiguration: copyThemeConfiguration(sourceThemeConfiguration),
			setMode: (mode) => {
				set((state) => ({
					mode,
					themeConfiguration: {
						...state.themeConfiguration,
						palette: {
							...state.themeConfiguration.palette,
							mode,
						},
					},
				}));
			},
			toggleColorMode: () => {
				set((state) => {
					const mode = state.mode === "light" ? "dark" : "light";

					return {
						mode,
						themeConfiguration: {
							...state.themeConfiguration,
							palette: {
								...state.themeConfiguration.palette,
								mode,
							},
						},
					};
				});
			},
			applyThemeConfiguration: (configuration) => {
				const normalizedConfiguration = normalizeThemeConfiguration(configuration);
				set({
					themeConfiguration: normalizedConfiguration,
					mode: normalizedConfiguration.palette.mode,
				});
			},
			resetThemeConfiguration: () => {
				const resetConfiguration = copyThemeConfiguration(sourceThemeConfiguration);
				set({
					themeConfiguration: resetConfiguration,
					mode: resetConfiguration.palette.mode,
				});
			},
		}),
		{
			name: "theme-storage",
			merge: (persistedState, currentState) => {
				const persistedRecord =
					typeof persistedState === "object" && persistedState !== null ? (persistedState as Record<string, unknown>) : {};

				const normalizedConfiguration = normalizeThemeConfiguration(persistedRecord["themeConfiguration"]);
				const mode = readPersistedMode(persistedRecord["mode"], normalizedConfiguration.palette.mode);

				return {
					...currentState,
					mode,
					themeConfiguration: normalizedConfiguration,
				};
			},
		},
	),
);
