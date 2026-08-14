import {
	alpha,
	Card,
	createTheme,
	defaultVariantColorsResolver,
	MantineProvider,
	parseThemeColor,
	type VariantColorsResolver,
} from "@mantine/core";
import { Notifications } from "@mantine/notifications";

import { useThemeStore } from "@/core/theme/stores/ThemeStore";
import type { ThemeProviderProperties } from "@/core/theme/provider/ThemeProvider.types";

export function ThemeProvider({ children }: ThemeProviderProperties) {
	const mode = useThemeStore((state) => state.mode);
	const themeConfiguration = useThemeStore((state) => state.themeConfiguration);
	const primaryScale = themeConfiguration.palette.primary.scale;
	const secondaryScale = themeConfiguration.palette.secondary.scale;
	const primaryMain = themeConfiguration.palette.primary.main;
	const secondaryMain = themeConfiguration.palette.secondary.main;
	const secondaryOutline = themeConfiguration.palette.secondary.outline;
	const breakpoints = themeConfiguration.breakpoints.values;

	const variantColorResolver: VariantColorsResolver = (input) => {
		const defaultResolvedColors = defaultVariantColorsResolver(input);

		if (input.variant !== "outline") {
			return defaultResolvedColors;
		}

		const parsed = parseThemeColor({ color: input.color, theme: input.theme });
		if (!parsed.isThemeColor || parsed.color !== "secondary" || parsed.shade !== undefined) {
			return defaultResolvedColors;
		}

		return {
			...defaultResolvedColors,
			color: secondaryOutline,
			border: `1px solid ${secondaryOutline}`,
			hover: alpha(secondaryOutline, 0.05),
		};
	};

	const theme = createTheme({
		fontFamily: themeConfiguration.typography.fontFamily,
		primaryColor: "primary",
		variantColorResolver,
		components: {
			// App-wide section-card look. Call sites can still opt out (withBorder={false}) or pick another
			// radius/padding; the defaults just make the common case the consistent case.
			Card: Card.extend({
				defaultProps: {
					withBorder: true,
					radius: "md",
					padding: "lg",
				},
			}),
		},
		colors: {
			primary: [
				primaryScale[0] ?? primaryMain,
				primaryScale[1] ?? primaryMain,
				primaryScale[2] ?? primaryMain,
				primaryScale[3] ?? primaryMain,
				primaryScale[4] ?? primaryMain,
				primaryScale[5] ?? primaryMain,
				primaryScale[6] ?? primaryMain,
				primaryScale[7] ?? primaryMain,
				primaryScale[8] ?? primaryMain,
				primaryScale[9] ?? primaryMain,
			],
			secondary: [
				secondaryScale[0] ?? secondaryMain,
				secondaryScale[1] ?? secondaryMain,
				secondaryScale[2] ?? secondaryMain,
				secondaryScale[3] ?? secondaryMain,
				secondaryScale[4] ?? secondaryMain,
				secondaryScale[5] ?? secondaryMain,
				secondaryScale[6] ?? secondaryMain,
				secondaryScale[7] ?? secondaryMain,
				secondaryScale[8] ?? secondaryMain,
				secondaryScale[9] ?? secondaryMain,
			],
		},
		breakpoints: {
			xs: `${breakpoints.xs / 16}em`,
			sm: `${breakpoints.sm / 16}em`,
			md: `${breakpoints.md / 16}em`,
			lg: `${breakpoints.lg / 16}em`,
			xl: `${breakpoints.xl / 16}em`,
		},
	});

	return (
		<MantineProvider theme={theme} forceColorScheme={mode}>
			<Notifications position="top-right" autoClose={5000} />
			{children}
		</MantineProvider>
	);
}
