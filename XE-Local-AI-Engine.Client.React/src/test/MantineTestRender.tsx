import { MantineProvider, type MantineThemeOverride } from "@mantine/core";
import { render } from "@testing-library/react";
import type { ReactElement } from "react";
import { vi } from "vitest";

const instantTransition = { defaultProps: { transitionProps: { duration: 0, exitDuration: 0 } } };

/**
 * Mantine's `env="test"` is not enough on its own. It only short-circuits what `<Transition>` RENDERS —
 * `useTransition` is called unconditionally above that check (`@mantine/core@9.6.1`, `Transition.tsx` /
 * `use-transition.ts`), so every close with a non-zero duration takes the async branch and schedules a real
 * `requestAnimationFrame` plus `setTimeout`. The hook cancels those only when the component hosting the
 * `Transition` unmounts, and an overlay keeps its `Transition` mounted while merely toggling the `mounted` prop.
 * Under load more than that duration of wall-clock time can pass between a dialog closing and `afterEach(cleanup)`,
 * so the timer fires into a torn-down tree: every test passes and the vitest process still exits 1.
 *
 * A zero duration takes `handleStateChange`'s synchronous branch instead — no rAF, no timeout, nothing to outlive
 * the test. Listed here are only the overlays this app uses whose own defaults are non-zero: `Modal` (200),
 * `Drawer` (250, `Transition`'s own default), `Popover` (150 — `Menu` renders a `Popover`, so it is covered by this
 * entry) and `Tooltip` (100). `Combobox` (Select, Autocomplete) and `LoadingOverlay` already default to 0.
 *
 * This is a test-only default: `useProps` applies caller props last, so a test that passes its own
 * `transitionProps` still wins, and shipped components are untouched.
 *
 * Deliberately NOT listed: `Collapse` (and `Accordion`/`NavLink`, which render their panels through it). It does not use
 * `Transition` at all — `useCollapse`'s `useDidUpdate` schedules one `requestAnimationFrame` per toggle ABOVE its
 * own `transitionDuration !== 0` check, so a zeroed duration does not stop it (measured: one pending frame either
 * way). That frame is harmless where a `Transition` timer is not: it returns at `!elementRef.current`, which is
 * exactly the post-unmount state, so it cannot touch a torn-down tree. Zeroing it would also make `Collapse` drop
 * collapsed content out of the DOM under `env="test"`, breaking the `aria-controls` regions and collapsed-height
 * styles the disclosure tests assert (measured by adding the entry: red in `SourceBuildCard` and `VariablesForm`).
 *
 * Latent, not live: `Popover`'s own `withOverlay` backdrop reads `transitionProps?.duration || 250`, so a zero is
 * falsy there and the theme would be ignored. No `<Popover withOverlay>` exists in `src`; pass the duration
 * explicitly if one is ever added.
 */
export const testMantineTheme: MantineThemeOverride = {
	components: {
		Modal: instantTransition,
		Drawer: instantTransition,
		Popover: instantTransition,
		Tooltip: instantTransition,
	},
};

// MantineProvider reads the color scheme through matchMedia on mount, several components measure themselves
// through ResizeObserver, and an autosize <Textarea> subscribes to `document.fonts` ("loadingdone" re-measures
// after a web font swaps in). jsdom implements none of the three, so every Mantine render needs these stubs
// installed first — without them the provider (or the first autosize Textarea) throws before the component under
// test ever renders. The FontFaceSet stub is why an autosize Textarea no longer fails with
// "Cannot read properties of undefined (reading 'addEventListener')".
export function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
	// jsdom has no layout engine, so Element.prototype.scrollIntoView does not exist. Mantine's combobox calls it on a
	// TIMER after the dropdown opens, which lands after the test that opened it has finished — an uncaught exception
	// rather than a failed assertion, and one that fails the run without naming a broken expectation.
	Object.defineProperty(Element.prototype, "scrollIntoView", {
		writable: true,
		configurable: true,
		value: vi.fn(),
	});
	Object.defineProperty(document, "fonts", {
		writable: true,
		configurable: true,
		value: {
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			ready: Promise.resolve(),
		},
	});
}

// Renders a component inside a bare MantineProvider, the minimum context every Mantine component needs.
export function renderWithMantine(ui: ReactElement) {
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			{ui}
		</MantineProvider>,
	);
}
