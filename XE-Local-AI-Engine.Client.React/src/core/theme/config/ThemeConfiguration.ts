import themeConfigurationJson from "@/theme/theme.json";

const defaultPrimaryScale = [
	"#fff0f0",
	"#ffdede",
	"#ffc2c2",
	"#ff9494",
	"#ff6363",
	"#f03e3e",
	"#de0a1b",
	"#c42a35",
	"#a61d2b",
	"#8c1524",
];
const defaultSecondaryScale = [
	"#fff2f2",
	"#ffe3e3",
	"#ffc9c9",
	"#ffa8a8",
	"#ff8787",
	"#ff6b6b",
	"#fa5252",
	"#f03e3e",
	"#e03131",
	"#c92a2a",
];
const defaultPrimaryMain = "#de0a1b";
const defaultSecondaryMain = "#fa5252";
const defaultPrimaryHover = "#c42a35";
const defaultSecondaryHover = "#f03e3e";
const defaultSecondaryOutline = "#fa5252";
const defaultBreakpoints = {
	xs: 0,
	sm: 640,
	md: 768,
	lg: 1024,
	xl: 1280,
};

export type ThemeMode = "light" | "dark";

export interface ThemeConfiguration {
	palette: {
		mode: ThemeMode;
		primary: {
			main: string;
			hover: string;
			scale: string[];
		};
		secondary: {
			main: string;
			hover: string;
			outline: string;
			scale: string[];
		};
	};
	typography: {
		fontFamily: string;
	};
	breakpoints: {
		values: {
			xs: number;
			sm: number;
			md: number;
			lg: number;
			xl: number;
		};
	};
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null;
}

function readString(value: unknown, fallback: string): string {
	if (typeof value !== "string") {
		return fallback;
	}

	const trimmed = value.trim();
	return trimmed.length > 0 ? trimmed : fallback;
}

function readNumber(value: unknown, fallback: number): number {
	return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function readScale(value: unknown, fallback: readonly string[]): string[] {
	if (!Array.isArray(value) || value.length !== 10) {
		return [...fallback];
	}

	const entries = value.map((entry, index) => {
		const fallbackValue = fallback[index] ?? fallback[0] ?? "";
		return readString(entry, fallbackValue);
	});
	return entries;
}

function readMode(value: unknown): ThemeMode {
	return value === "dark" ? "dark" : "light";
}

function readPalette(input: Record<string, unknown>): ThemeConfiguration["palette"] {
	const palette = isRecord(input["palette"]) ? input["palette"] : {};
	const primary = isRecord(palette["primary"]) ? palette["primary"] : {};
	const secondary = isRecord(palette["secondary"]) ? palette["secondary"] : {};

	const primaryScale = readScale(primary["scale"], defaultPrimaryScale);
	const secondaryScale = readScale(secondary["scale"], defaultSecondaryScale);
	const primaryMain = readString(primary["main"], primaryScale[6] ?? defaultPrimaryMain);
	const secondaryMain = readString(secondary["main"], defaultSecondaryMain);
	const primaryHover = readString(primary["hover"], primaryScale[7] ?? defaultPrimaryHover);
	const secondaryHover = readString(secondary["hover"], defaultSecondaryHover);
	const secondaryOutline = readString(secondary["outline"], secondaryMain || defaultSecondaryOutline);

	const normalizedPrimaryScale = [...primaryScale];
	normalizedPrimaryScale[6] = primaryMain;
	normalizedPrimaryScale[8] = primaryMain;
	normalizedPrimaryScale[7] = primaryHover;
	normalizedPrimaryScale[9] = primaryHover;

	const normalizedSecondaryScale = [...secondaryScale];
	normalizedSecondaryScale[6] = secondaryMain;
	normalizedSecondaryScale[8] = secondaryMain;
	normalizedSecondaryScale[7] = secondaryHover;
	normalizedSecondaryScale[9] = secondaryHover;

	return {
		mode: readMode(palette["mode"]),
		primary: {
			main: primaryMain,
			hover: primaryHover,
			scale: normalizedPrimaryScale,
		},
		secondary: {
			main: secondaryMain,
			hover: secondaryHover,
			outline: secondaryOutline,
			scale: normalizedSecondaryScale,
		},
	};
}

function readTypography(input: Record<string, unknown>) {
	const typography = isRecord(input["typography"]) ? input["typography"] : {};

	return {
		fontFamily: readString(typography["fontFamily"], '"Roboto", "Helvetica", "Arial", sans-serif'),
	};
}

function readBreakpoints(input: Record<string, unknown>) {
	const breakpoints = isRecord(input["breakpoints"]) ? input["breakpoints"] : {};
	const values = isRecord(breakpoints["values"]) ? breakpoints["values"] : {};

	return {
		values: {
			xs: readNumber(values["xs"], defaultBreakpoints.xs),
			sm: readNumber(values["sm"], defaultBreakpoints.sm),
			md: readNumber(values["md"], defaultBreakpoints.md),
			lg: readNumber(values["lg"], defaultBreakpoints.lg),
			xl: readNumber(values["xl"], defaultBreakpoints.xl),
		},
	};
}

export function normalizeThemeConfiguration(input: unknown): ThemeConfiguration {
	const source = isRecord(input) ? input : {};

	return {
		palette: readPalette(source),
		typography: readTypography(source),
		breakpoints: readBreakpoints(source),
	};
}

export const sourceThemeConfiguration = normalizeThemeConfiguration(themeConfigurationJson);
