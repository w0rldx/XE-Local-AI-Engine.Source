import { describe, expect, it } from "vitest";

import { navVariants } from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar.animations";
import {
	COMPACT_CONTROLS_BREAKPOINT,
	DESKTOP_NAV_BREAKPOINT,
	DESKTOP_NAV_MIN_HEIGHT,
	isCompactViewport,
	isDesktopShell,
	SIDEBAR_WIDTH_COLLAPSED,
	SIDEBAR_WIDTH_EXPANDED,
	TWO_PANE_BREAKPOINT,
} from "@/core/layout/constants/LayoutBreakpoints";
import { sourceThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";

// The layout constants are literals (see the module header for why they are not read from the theme at runtime).
// These assertions are the cross-check: retuning the Mantine breakpoints without deciding what the app shell should
// do fails here instead of silently splitting the JS layout from the CSS/UnoCSS one.
describe("layout breakpoints", () => {
	it("matches the theme sm breakpoint for the compact-controls cutoff", () => {
		expect(COMPACT_CONTROLS_BREAKPOINT).toBe(sourceThemeConfiguration.breakpoints.values.sm);
	});

	// Moved from md to lg on purpose: at md the sidebar left a portrait tablet too little content width.
	it("matches the theme lg breakpoint for the desktop navigation cutoff", () => {
		expect(DESKTOP_NAV_BREAKPOINT).toBe(sourceThemeConfiguration.breakpoints.values.lg);
	});

	it("matches the theme lg breakpoint for the two-pane cutoff", () => {
		expect(TWO_PANE_BREAKPOINT).toBe(sourceThemeConfiguration.breakpoints.values.lg);
	});

	it("is the single source of the sidebar widths the navigation animates between", () => {
		expect(navVariants["expanded"]).toMatchObject({ width: SIDEBAR_WIDTH_EXPANDED });
		expect(navVariants["collapsed"]).toMatchObject({ width: SIDEBAR_WIDTH_COLLAPSED });
	});
});

// The viewports the 2026-10-23 layout audit swept; each one's expected shell is the decision the predicate encodes.
describe("isDesktopShell", () => {
	it.each([
		[1440, 900, true],
		[1280, 720, true],
		[1024, 768, true],
		[DESKTOP_NAV_BREAKPOINT, DESKTOP_NAV_MIN_HEIGHT, true],
		[DESKTOP_NAV_BREAKPOINT - 1, 900, false],
		[1280, DESKTOP_NAV_MIN_HEIGHT - 1, false],
		[768, 1024, false],
		[844, 390, false],
		[390, 844, false],
	])("%i×%i gets the desktop shell: %s", (width, height, expected) => {
		expect(isDesktopShell(width, height)).toBe(expected);
	});
});

describe("isCompactViewport", () => {
	it.each([
		[360, 640, true],
		[COMPACT_CONTROLS_BREAKPOINT - 1, 1024, true],
		[844, 390, true],
		[1280, DESKTOP_NAV_MIN_HEIGHT - 1, true],
		[COMPACT_CONTROLS_BREAKPOINT, DESKTOP_NAV_MIN_HEIGHT, false],
		[768, 1024, false],
		[1440, 900, false],
	])("%i×%i is compact: %s", (width, height, expected) => {
		expect(isCompactViewport(width, height)).toBe(expected);
	});
});
