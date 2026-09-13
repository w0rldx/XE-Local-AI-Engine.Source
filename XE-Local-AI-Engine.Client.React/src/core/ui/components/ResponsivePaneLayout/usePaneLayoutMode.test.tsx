// @vitest-environment jsdom

import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { usePaneLayoutMode } from "@/core/ui/components/ResponsivePaneLayout/usePaneLayoutMode";

/**
 * A capturing ResizeObserver: the shared `installJsdomEnvironmentMocks` stub has no-op methods, which is right for
 * every other test but leaves no way to deliver a resize. This one hands the callback back so a test can fire one.
 * Installed locally and restored afterwards, so nothing else changes.
 */
let fireResize: ((width: number) => void) | undefined;
const realResizeObserver = globalThis.ResizeObserver;

/**
 * jsdom has no layout: every element reports `clientWidth` 0. The property is stubbed on the prototype because the
 * hook measures in a layout effect, before a test could ever get a handle on the node.
 */
let stubbedClientWidth = 0;

function setViewportWidth(width: number): void {
	Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: width });
}

/** The whole surface under test: what the hook decided, and the element it measured to decide it. */
function Probe({ padding }: { readonly padding?: string }) {
	const { ref, isNarrow } = usePaneLayoutMode();
	return (
		<div ref={ref} data-testid="container" style={padding === undefined ? undefined : { padding }}>
			<span data-testid="mode">{isNarrow ? "narrow" : "wide"}</span>
		</div>
	);
}

function mode(): string {
	return screen.getByTestId("mode").textContent ?? "";
}

describe("usePaneLayoutMode", () => {
	beforeEach(() => {
		stubbedClientWidth = 0;
		Object.defineProperty(HTMLElement.prototype, "clientWidth", {
			configurable: true,
			get: () => stubbedClientWidth,
		});

		globalThis.ResizeObserver = class {
			readonly callback: ResizeObserverCallback;

			constructor(callback: ResizeObserverCallback) {
				this.callback = callback;
			}

			observe(): void {
				// The hook re-reads `clientWidth` and ignores the entries, so a resize IS a new stubbed width plus a
				// callback; handing over an entry the hook never looks at would test the stub, not the hook.
				fireResize = (width: number) => {
					stubbedClientWidth = width;
					this.callback([], this as unknown as ResizeObserver);
				};
			}

			unobserve(): void {
				// The hook only ever disconnects; nothing under test calls this.
			}

			disconnect(): void {
				fireResize = undefined;
			}
		} as unknown as typeof ResizeObserver;
	});

	afterEach(() => {
		cleanup();
		fireResize = undefined;
		globalThis.ResizeObserver = realResizeObserver;
		// jsdom's own definition is a plain getter on the prototype; deleting the stub restores it.
		Reflect.deleteProperty(HTMLElement.prototype, "clientWidth");
	});

	// The fallback, and the only branch jsdom reaches without help: an unmeasured container keeps the pre-existing
	// viewport rule so a page never renders against a width of zero.
	it("falls back to the viewport rule while the container is unmeasured", () => {
		setViewportWidth(1023);
		render(<Probe />);

		expect(mode()).toBe("narrow");
	});

	it("is wide at the viewport breakpoint while the container is unmeasured", () => {
		setViewportWidth(1024);
		render(<Probe />);

		expect(mode()).toBe("wide");
	});

	// The defect this hook exists for: the grid sits inside the app shell, so the viewport is not the space the panes
	// get. Once the container has a width of its own the viewport must not get a vote at all.
	it("goes narrow on a wide viewport when the container cannot hold three columns", () => {
		setViewportWidth(2000);
		stubbedClientWidth = 700;
		render(<Probe />);

		expect(mode()).toBe("narrow");
	});

	it("goes wide on a narrow viewport when the container can hold three columns", () => {
		setViewportWidth(800);
		stubbedClientWidth = 1000;
		render(<Probe />);

		expect(mode()).toBe("wide");
	});

	// 972 = 320 list + 240 centre floor + 380 side floor + two 16px gaps, derived in PaneLayoutTracks so the
	// threshold and the tracks cannot drift apart.
	it("switches at the three-column minimum of 972px", () => {
		setViewportWidth(2000);
		stubbedClientWidth = 971;
		const { unmount } = render(<Probe />);
		expect(mode()).toBe("narrow");
		unmount();

		stubbedClientWidth = 972;
		render(<Probe />);
		expect(mode()).toBe("wide");
	});

	// `WIDE_PANE_MIN_WIDTH` is the width of the tracks themselves, so the hook has to hand the grid the CONTENT box.
	// Reading the padding box would promise room the grid never gets and let it overflow — the defect the floors and
	// this threshold exist to prevent. jsdom reports no layout, so the padding is what `getComputedStyle` reflects
	// from the inline style while `clientWidth` stays stubbed.
	it("subtracts the container's horizontal padding before comparing with the three-column minimum", () => {
		setViewportWidth(2000);
		stubbedClientWidth = 980;
		render(<Probe padding="0 8px" />);

		// 980 padding box − 16 padding = 964 of content, four short of the 972 the three columns need.
		expect(mode()).toBe("narrow");
	});

	// The sidebar collapses with no window resize at all, so a ResizeObserver report is the only signal that the room
	// changed. Without this the page would stay narrow until something else happened to re-render it.
	it("flips when the container is resized without the window changing", () => {
		setViewportWidth(2000);
		stubbedClientWidth = 700;
		render(<Probe />);
		expect(mode()).toBe("narrow");

		act(() => {
			fireResize?.(1200);
		});

		expect(mode()).toBe("wide");
	});
});
