import { describe, expect, it } from "vitest";

import {
	DESKTOP_NAV_BREAKPOINT,
	SIDEBAR_WIDTH_COLLAPSED,
	SIDEBAR_WIDTH_EXPANDED,
	TWO_PANE_BREAKPOINT,
} from "@/core/layout/constants/LayoutBreakpoints";
import { navVariants } from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar.animations";
import { sourceThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";

// The layout constants are literals (see the module header for why they are not read from the theme at runtime).
// These assertions are the cross-check: retuning the Mantine breakpoints without deciding what the app shell should
// do fails here instead of silently splitting the JS layout from the CSS/UnoCSS one.
describe("layout breakpoints", () => {
	it("matches the theme md breakpoint for the desktop navigation cutoff", () => {
		expect(DESKTOP_NAV_BREAKPOINT).toBe(sourceThemeConfiguration.breakpoints.values.md);
	});

	it("matches the theme lg breakpoint for the two-pane cutoff", () => {
		expect(TWO_PANE_BREAKPOINT).toBe(sourceThemeConfiguration.breakpoints.values.lg);
	});

	it("is the single source of the sidebar widths the navigation animates between", () => {
		expect(navVariants["expanded"]).toMatchObject({ width: SIDEBAR_WIDTH_EXPANDED });
		expect(navVariants["collapsed"]).toMatchObject({ width: SIDEBAR_WIDTH_COLLAPSED });
	});
});
