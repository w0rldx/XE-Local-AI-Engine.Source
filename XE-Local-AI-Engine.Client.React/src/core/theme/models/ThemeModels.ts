import type { ThemeConfiguration, ThemeMode } from "@/core/theme/config/ThemeConfiguration";

export interface ThemeState {
	mode: ThemeMode;
	themeConfiguration: ThemeConfiguration;
	setMode: (_mode: ThemeMode) => void;
	toggleColorMode: () => void;
	applyThemeConfiguration: (_configuration: ThemeConfiguration) => void;
	resetThemeConfiguration: () => void;
}
