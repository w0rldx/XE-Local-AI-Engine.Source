import { alpha, createTheme, defaultVariantColorsResolver, parseThemeColor, type VariantColorsResolver } from "@mantine/core";

import type { ThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";
import type { PaletteKey } from "@/modules/theme-configurator/components/ThemeConfigurator.types";
import {
	buildGeneratedScale,
	defaultPrimaryBaseColor,
	defaultSecondaryBaseColor,
	parseColor,
	readScale,
} from "@/modules/theme-configurator/components/ColorUtils";

export function withGeneratedPaletteScale(
	currentDraft: ThemeConfiguration,
	paletteKey: PaletteKey,
	baseColorInput: string,
): ThemeConfiguration {
	const fallback = paletteKey === "primary" ? defaultPrimaryBaseColor : defaultSecondaryBaseColor;
	const baseColor = parseColor(baseColorInput, fallback);
	const scale = buildGeneratedScale(baseColor);
	const main = scale[6] ?? baseColor;
	const hover = scale[7] ?? main;
	const synchronizedScale = [...scale];
	synchronizedScale[6] = main;
	synchronizedScale[8] = main;
	synchronizedScale[7] = hover;
	synchronizedScale[9] = hover;

	if (paletteKey === "primary") {
		return {
			...currentDraft,
			palette: {
				...currentDraft.palette,
				primary: {
					...currentDraft.palette.primary,
					main,
					hover,
					scale: synchronizedScale,
				},
			},
		};
	}

	return {
		...currentDraft,
		palette: {
			...currentDraft.palette,
			secondary: {
				...currentDraft.palette.secondary,
				main,
				hover,
				outline: main,
				scale: synchronizedScale,
			},
		},
	};
}

function resolvePreviewVariantColors(
	input: Parameters<VariantColorsResolver>[0],
	primaryMain: string,
	primaryHover: string,
	secondaryMain: string,
	secondaryHover: string,
	secondaryOutline: string,
) {
	const defaultResolvedColors = defaultVariantColorsResolver(input);
	const parsed = parseThemeColor({ color: input.color, theme: input.theme });

	if (!parsed.isThemeColor || parsed.shade !== undefined) {
		return defaultResolvedColors;
	}

	const isPrimaryColor = parsed.color === "primary";
	const isSecondaryColor = parsed.color === "secondary";
	if (!isPrimaryColor && !isSecondaryColor) {
		return defaultResolvedColors;
	}

	const main = isPrimaryColor ? primaryMain : secondaryMain;
	const hover = isPrimaryColor ? primaryHover : secondaryHover;

	if (input.variant === "filled") {
		return {
			...defaultResolvedColors,
			color: "var(--mantine-color-white)",
			background: main,
			border: "1px solid transparent",
			hover,
		};
	}

	if (input.variant === "light") {
		return {
			...defaultResolvedColors,
			color: main,
			background: alpha(main, 0.1),
			border: "1px solid transparent",
			hover: alpha(main, 0.12),
		};
	}

	if (input.variant === "subtle") {
		return {
			...defaultResolvedColors,
			color: main,
			background: "transparent",
			border: "1px solid transparent",
			hover: alpha(main, 0.12),
		};
	}

	if (input.variant !== "outline") {
		return defaultResolvedColors;
	}

	const outlineColor = isSecondaryColor ? secondaryOutline : main;

	return {
		...defaultResolvedColors,
		color: outlineColor,
		border: `1px solid ${outlineColor}`,
		hover: alpha(outlineColor, 0.05),
	};
}

export function createDraftPreviewTheme(themeConfiguration: ThemeConfiguration) {
	const primaryMain = parseColor(themeConfiguration.palette.primary.main, defaultPrimaryBaseColor);
	const primaryHover = parseColor(themeConfiguration.palette.primary.hover, primaryMain);
	const secondaryMain = parseColor(themeConfiguration.palette.secondary.main, defaultSecondaryBaseColor);
	const secondaryHover = parseColor(themeConfiguration.palette.secondary.hover, secondaryMain);
	const secondaryOutline = parseColor(themeConfiguration.palette.secondary.outline, secondaryMain);
	const primaryScale = readScale(themeConfiguration.palette.primary.scale, primaryMain);
	const secondaryScale = readScale(themeConfiguration.palette.secondary.scale, secondaryMain);

	const variantColorResolver: VariantColorsResolver = (input) =>
		resolvePreviewVariantColors(input, primaryMain, primaryHover, secondaryMain, secondaryHover, secondaryOutline);

	return createTheme({
		fontFamily: themeConfiguration.typography.fontFamily,
		primaryColor: "primary",
		variantColorResolver,
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
	});
}
