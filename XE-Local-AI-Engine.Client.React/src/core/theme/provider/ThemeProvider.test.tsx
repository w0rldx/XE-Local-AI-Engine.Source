// @vitest-environment jsdom

import { Alert, PasswordInput, Table, useMantineColorScheme, useMantineTheme } from "@mantine/core";
import { cleanup, render, renderHook, screen } from "@testing-library/react";
import i18next from "i18next";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { sourceThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";
import { ThemeProvider } from "@/core/theme/provider/ThemeProvider";
import { useThemeStore } from "@/core/theme/stores/ThemeStore";
import de from "@/locales/de.json";
import { installJsdomEnvironmentMocks } from "@/test/MantineTestRender";

describe("ThemeProvider table scroll affordance", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	// Mantine's ScrollArea defaults to type="hover": the scrollbar only appears once a pointer enters the region, so a
	// touch device gets no affordance at all and an overflowing table just looks clipped. "auto" shows the bar whenever
	// the content overflows, on any input device, and shows nothing when it fits. Asserted at the theme level because
	// the fix is app-wide (35+ tables) rather than per call site.
	it("defaults every Table.ScrollContainer to an overflow-driven scrollbar", () => {
		// Reads the theme the provider actually hands to MantineProvider, which is what every Table.ScrollContainer in
		// the app resolves against (useProps merges component defaults -> theme defaults -> call-site props).
		const { result } = renderHook(() => useMantineTheme(), { wrapper: ThemeProvider });
		const defaultProps = result.current.components["TableScrollContainer"]?.defaultProps as
			| { scrollAreaProps?: { type?: string } }
			| undefined;

		expect(defaultProps?.scrollAreaProps?.type).toBe("auto");
	});

	// The prop above is only worth anything if the container still renders a ScrollArea (type="native" would ignore
	// scrollAreaProps entirely) and actually mounts a horizontal scrollbar for the overflow direction tables clip in.
	it("renders a horizontal scrollbar element for a table wrapped in a scroll container", () => {
		const { container } = render(
			<ThemeProvider>
				<Table.ScrollContainer minWidth={500}>
					<Table>
						<Table.Tbody>
							<Table.Tr>
								<Table.Td>cell</Table.Td>
							</Table.Tr>
						</Table.Tbody>
					</Table>
				</Table.ScrollContainer>
			</ThemeProvider>,
		);

		const horizontalScrollbar = container.querySelector('.mantine-ScrollArea-scrollbar[data-orientation="horizontal"]');
		expect(horizontalScrollbar).not.toBeNull();
	});

	// Same affordance one level down, for the ScrollAreas the app mounts directly. ScrollArea and
	// ScrollArea.Autosize read SEPARATE theme keys, so declaring only "ScrollArea" would leave every autosize
	// call site on Mantine's 12px hover bar — which is the regression this pins.
	it.each(["ScrollArea", "ScrollAreaAutosize"])("defaults %s to a thin, overflow-driven, offset scrollbar", (key) => {
		const { result } = renderHook(() => useMantineTheme(), { wrapper: ThemeProvider });
		const defaultProps = result.current.components[key]?.defaultProps as
			| { type?: string; scrollbarSize?: number; offsetScrollbars?: boolean | string }
			| undefined;

		expect(defaultProps?.type).toBe("auto");
		expect(defaultProps?.scrollbarSize).toBe(8);
		expect(defaultProps?.offsetScrollbars).toBe("present");
	});
});

// Reads the CSS-variable block MantineProvider injects for one colour scheme. The provider renders the variables
// as real <style> text (one rule per scheme selector), so this is the shipped value, not the resolver's input.
function readColorSchemeVariables(scheme: "light" | "dark"): string {
	const selector = `:root[data-mantine-color-scheme="${scheme}"]`;
	const rule = [...document.querySelectorAll("style")]
		.flatMap((element) => (element.textContent ?? "").split("}"))
		.find((block) => block.includes(selector));

	return rule ?? "";
}

describe("ThemeProvider dimmed text contrast", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	// Mantine's light-mode dimmed is gray-6 on white — about 3.3:1, under the 4.5:1 WCAG 1.4.3 floor for the
	// secondary text every page renders with `c="dimmed"`. gray-7 clears it. Asserted on the emitted variable
	// because that is what the call sites resolve against; a resolver that returns the right object but is never
	// handed to MantineProvider would still pass a check of the resolver alone.
	it("raises the light-mode dimmed colour to a scale step that passes contrast", () => {
		render(<ThemeProvider>content</ThemeProvider>);

		expect(readColorSchemeVariables("light")).toContain("--mantine-color-dimmed: var(--mantine-color-gray-7)");
	});

	// Dark mode already passes (dark-2 on the dark surfaces) and darkening it there would make the text worse, so
	// the override is light-only: the dark block must not carry a dimmed declaration of its own.
	it("leaves the dark-mode dimmed colour to Mantine", () => {
		render(<ThemeProvider>content</ThemeProvider>);

		const darkVariables = readColorSchemeVariables("dark");
		// An empty block would make the assertion below vacuous, so prove the dark rule was emitted at all first.
		expect(darkVariables).toContain("--mantine-color-anchor");
		expect(darkVariables).not.toContain("--mantine-color-dimmed");
	});
});

describe("ThemeProvider alert close button", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	// Mantine's Alert close button is icon-only; without `closeButtonLabel` it reaches a screen reader as a
	// nameless "button". Only one of the app's `withCloseButton` call sites passed one, so the default is set in
	// the theme and asserted here — at the level the fix actually lives.
	it("gives every Alert close button the shipped Close label", () => {
		const { result } = renderHook(() => useMantineTheme(), { wrapper: ThemeProvider });
		const defaultProps = result.current.components["Alert"]?.defaultProps as { closeButtonLabel?: string } | undefined;

		expect(defaultProps?.closeButtonLabel).toBe("Close");
	});

	// The theme value is only worth anything if it reaches the rendered button, which is what names it.
	it("renders a named close button for an Alert that asks for one", () => {
		render(
			<ThemeProvider>
				<Alert withCloseButton={true} title="Heads up">
					body
				</Alert>
			</ThemeProvider>,
		);

		expect(screen.getByRole("button", { name: "Close" })).toBeTruthy();
	});
});

describe("ThemeProvider password visibility toggle", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(async () => {
		cleanup();
		await i18next.changeLanguage("en");
	});

	// Mantine names the eye button in English whatever the UI language; the theme default carries the translation.
	it("names the eye button in the operator's language", async () => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");

		render(
			<ThemeProvider>
				<PasswordInput label="Passwort" />
			</ThemeProvider>,
		);

		expect(screen.getByRole("button", { name: "Passwort-Sichtbarkeit umschalten" })).toBeTruthy();
	});
});

describe("ThemeProvider accent colour", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		useThemeStore.setState({ accentColor: null });
	});

	// Mantine owns the scheme now: with nothing stored it must follow the OS ("auto"), not a value forced by the app.
	it("defaults the colour scheme to auto", () => {
		localStorage.removeItem("mantine-color-scheme");

		const { result } = renderHook(() => useMantineColorScheme(), { wrapper: ThemeProvider });

		expect(result.current.colorScheme).toBe("auto");
	});

	// A user who never picks an accent must see exactly the shipped theme.json primary scale.
	it("uses the shipped primary scale when no accent is chosen", () => {
		useThemeStore.setState({ accentColor: null });

		const { result } = renderHook(() => useMantineTheme(), { wrapper: ThemeProvider });

		expect(result.current.colors["primary"]).toEqual(sourceThemeConfiguration.palette.primary.scale);
	});

	it("generates a full ten-shade primary scale from a chosen accent", () => {
		useThemeStore.setState({ accentColor: "#1c7ed6" });

		const { result } = renderHook(() => useMantineTheme(), { wrapper: ThemeProvider });
		const primary = result.current.colors["primary"];

		expect(primary).toHaveLength(10);
		expect(primary?.[6]).not.toBe(sourceThemeConfiguration.palette.primary.scale[6]);
	});
});

describe("ThemeProvider floating dropdown cap", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	interface SizeMiddleware {
		readonly padding?: number;
		readonly apply: (state: { availableWidth: number; availableHeight: number; elements: { floating: HTMLElement } }) => void;
	}

	function popoverMiddlewares(): { flip?: boolean; shift?: boolean; size?: SizeMiddleware } | undefined {
		const { result } = renderHook(() => useMantineTheme(), { wrapper: ThemeProvider });
		const defaultProps = result.current.components["Popover"]?.defaultProps as
			| { middlewares?: { flip?: boolean; shift?: boolean; size?: SizeMiddleware } }
			| undefined;
		return defaultProps?.middlewares;
	}

	function sizeMiddleware(): SizeMiddleware | undefined {
		return popoverMiddlewares()?.size;
	}

	// The default replaces Mantine's whole middleware object, so it has to keep Mantine's own flip and shift.
	it("keeps flip and shift beside the size cap", () => {
		const middlewares = popoverMiddlewares();

		expect(middlewares?.flip).toBe(true);
		expect(middlewares?.shift).toBe(true);
	});

	function floatingWithContentHeight(height: number): HTMLElement {
		const floating = document.createElement("div");
		Object.defineProperty(floating, "scrollHeight", { configurable: true, value: height });
		return floating;
	}

	// On a phone in landscape neither side of a trigger had room for a Menu, Select or Popover dropdown, which then
	// ran past the top or bottom of the screen. Every one of them renders a Popover, so the cap lives in its theme default.
	it("caps a dropdown taller than the room it has and lets it scroll", () => {
		const size = sizeMiddleware();
		const floating = floatingWithContentHeight(500);

		size?.apply({ availableWidth: 820, availableHeight: 180, elements: { floating } });

		expect(size?.padding).toBe(8);
		expect(floating.style.maxHeight).toBe("180px");
		expect(floating.style.maxWidth).toBe("820px");
		expect(floating.style.overflowY).toBe("auto");
	});

	// A dropdown that fits keeps visible overflow, so a popover's arrow outside its box is not clipped.
	it("leaves a dropdown that fits unscrolled", () => {
		const size = sizeMiddleware();
		const floating = floatingWithContentHeight(120);

		size?.apply({ availableWidth: 820, availableHeight: 180, elements: { floating } });

		expect(floating.style.maxHeight).toBe("180px");
		expect(floating.style.overflowY).toBe("");
	});
});
