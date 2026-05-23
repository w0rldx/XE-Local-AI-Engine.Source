import { useMantineColorScheme } from "@mantine/core";

import type { AppTheme } from "@/core/theme/models/AppTheme";
import { useThemeStore } from "@/core/theme/stores/ThemeStore";

export function useAppTheme(): AppTheme {
	const { colorScheme } = useMantineColorScheme();
	const mode: "light" | "dark" = colorScheme === "dark" ? "dark" : "light";
	const themeConfiguration = useThemeStore((state) => state.themeConfiguration);
	const breakpoints = themeConfiguration.breakpoints.values;

	return {
		palette: {
			mode,
			primary: { main: themeConfiguration.palette.primary.main },
			secondary: { main: themeConfiguration.palette.secondary.main },
			divider: mode === "dark" ? "rgba(255,255,255,0.12)" : "rgba(0,0,0,0.12)",
			text: {
				primary: mode === "dark" ? "#fff" : "#111",
				secondary: mode === "dark" ? "rgba(255,255,255,0.7)" : "rgba(0,0,0,0.6)",
			},
			background: {
				default: mode === "dark" ? "#121212" : "#ffffff",
				paper: mode === "dark" ? "#1e1e1e" : "#ffffff",
			},
			action: {
				selected: mode === "dark" ? "rgba(255,255,255,0.08)" : "rgba(0,0,0,0.04)",
				focus: mode === "dark" ? "rgba(255,255,255,0.12)" : "rgba(0,0,0,0.08)",
				hover: mode === "dark" ? "rgba(255,255,255,0.1)" : "rgba(0,0,0,0.06)",
				selectedOpacity: 0.08,
			},
			grey: ["#fafafa", "#f5f5f5", "#eeeeee", "#e0e0e0", "#bdbdbd", "#9e9e9e", "#757575", "#616161", "#424242", "#212121"],
		},
		typography: {
			body2: {
				fontSize: "0.875rem",
				fontWeight: 400,
				lineHeight: 1.43,
			},
		},
		shape: {
			borderRadius: 8,
		},
		zIndex: {
			drawer: 300,
		},
		shadows: ["none", "0 1px 3px rgba(0,0,0,0.2)", "0 2px 8px rgba(0,0,0,0.2)"],
		spacing: (factor: number) => `${factor * 8}px`,
		transitions: {
			duration: { short: 200 },
			create: () => "all 200ms ease",
		},
		breakpoints: {
			up: (value: number) => `@media (minWidth:${value}px)`,
			down: (value: string | number) => {
				if (typeof value === "number") {
					return `@media (max-width:${value}px)`;
				}
				const map: Record<string, number> = {
					xs: breakpoints.xs,
					sm: breakpoints.sm,
					md: breakpoints.md,
					lg: breakpoints.lg,
					xl: breakpoints.xl,
				};
				return `@media (max-width:${(map[value] ?? 768) - 0.02}px)`;
			},
		},
		applyStyles: (targetMode, styles) => (targetMode === mode ? styles : {}),
	};
}
