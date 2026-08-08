import { generateColors } from "@mantine/colors-generator";

import type { ThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";

export const defaultPrimaryBaseColor = "#de0a1b";
export const defaultSecondaryBaseColor = "#fa5252";
export const previewScopeSelector = ".theme-configurator-preview-scope";
export const paletteIndexes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9] as const;

export const variantPreviewItems = [
	{ variant: "filled", labelKey: "pages.userSettings.themeConfigurator.variantFilled" },
	{ variant: "light", labelKey: "pages.userSettings.themeConfigurator.variantLight" },
	{ variant: "outline", labelKey: "pages.userSettings.themeConfigurator.variantOutline" },
	{ variant: "subtle", labelKey: "pages.userSettings.themeConfigurator.variantSubtle" },
] as const;

export function downloadThemeConfiguration(themeConfiguration: ThemeConfiguration) {
	const json = JSON.stringify(themeConfiguration, null, 2);
	const blob = new Blob([json], { type: "application/json" });
	const url = URL.createObjectURL(blob);
	const anchor = document.createElement("a");
	anchor.href = url;
	anchor.download = "theme.json";
	document.body.append(anchor);
	anchor.click();
	anchor.remove();
	URL.revokeObjectURL(url);
}

export function tryParseColor(input: unknown): string | null {
	if (typeof input !== "string") {
		return null;
	}

	const value = input.trim();
	if (/^#[0-9a-fA-F]{6}$/.test(value)) {
		return value.toLowerCase();
	}

	const rgbMatch = value.match(/^rgb\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$/);
	if (!rgbMatch) {
		return null;
	}

	const redText = rgbMatch[1];
	const greenText = rgbMatch[2];
	const blueText = rgbMatch[3];
	if (!redText || !greenText || !blueText) {
		return null;
	}

	const red = Number(redText);
	const green = Number(greenText);
	const blue = Number(blueText);
	if (![red, green, blue].every((channel) => Number.isInteger(channel) && channel >= 0 && channel <= 255)) {
		return null;
	}

	const toHex = (channel: number) => channel.toString(16).padStart(2, "0");
	return `#${toHex(red)}${toHex(green)}${toHex(blue)}`;
}

export function parseColor(input: unknown, fallback: string): string {
	return tryParseColor(input) ?? fallback;
}

export function readScale(scale: string[], fallback: string): string[] {
	return paletteIndexes.map((index) => parseColor(scale[index], fallback));
}

export function buildGeneratedScale(baseColor: string): string[] {
	const generated = [...generateColors(baseColor)];
	return readScale(generated, baseColor);
}
