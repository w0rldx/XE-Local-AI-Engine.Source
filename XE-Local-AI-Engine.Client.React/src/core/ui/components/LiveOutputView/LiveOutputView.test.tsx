// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	appendLiveOutputLines,
	type LiveOutputLine,
	liveOutputLineCap,
} from "@/core/ui/components/LiveOutputView/LiveOutputLines";
import { LiveOutputView } from "@/core/ui/components/LiveOutputView/LiveOutputView";
import { installJsdomEnvironmentMocks, renderWithMantine, testMantineTheme } from "@/test/MantineTestRender";

// `rerender` replaces the whole tree, so it needs the provider renderWithMantine wrapped the first render in.
function withMantine(ui: ReactElement) {
	return (
		<MantineProvider env="test" theme={testMantineTheme}>
			{ui}
		</MantineProvider>
	);
}

function stdout(count: number, prefix = "line"): LiveOutputLine[] {
	return Array.from({ length: count }, (_, index) => ({ stream: "stdout", text: `${prefix} ${index + 1}` }));
}

/** jsdom has no layout: give the log viewport a scroll geometry the test controls. */
function scrollGeometry(viewport: HTMLElement, scrollHeight: number, clientHeight: number) {
	const state = { scrollTop: 0, scrollHeight };
	Object.defineProperty(viewport, "scrollHeight", { configurable: true, get: () => state.scrollHeight });
	Object.defineProperty(viewport, "clientHeight", { configurable: true, get: () => clientHeight });
	Object.defineProperty(viewport, "scrollTop", {
		configurable: true,
		get: () => state.scrollTop,
		set: (value: number) => {
			state.scrollTop = value;
		},
	});
	return state;
}

describe("appendLiveOutputLines", () => {
	it("appends, caps at the client limit counting what it dropped, and starts over on replace", () => {
		const first = appendLiveOutputLines(undefined, stdout(liveOutputLineCap - 1), false);
		const capped = appendLiveOutputLines(first, stdout(3, "more"), false);

		expect(capped.lines).toHaveLength(liveOutputLineCap);
		expect(capped.hiddenLines).toBe(2);
		expect(capped.lines.at(-1)?.text).toBe("more 3");

		const replaced = appendLiveOutputLines(capped, stdout(1, "fresh"), true);
		expect(replaced).toEqual({ lines: [{ stream: "stdout", text: "fresh 1" }], hiddenLines: 0 });
	});
});

describe("LiveOutputView", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		vi.useRealTimers();
	});

	it("is a named, focusable log region that screen readers read on demand rather than per line", () => {
		renderWithMantine(<LiveOutputView lines={stdout(2)} running={false} label="Output of run_python" />);

		const log = screen.getByRole("log", { name: "Output of run_python" });
		expect(log.getAttribute("aria-live")).toBe("off");
		expect(log.getAttribute("tabindex")).toBe("0");
	});

	it("renders an HTML-looking line as literal text", () => {
		const markup = '<b>bold</b><img src="x" onerror="alert(1)">';
		renderWithMantine(<LiveOutputView lines={[{ stream: "stdout", text: markup }]} running={false} label="Output" />);

		const log = screen.getByRole("log");
		expect(log.textContent).toContain(markup);
		expect(log.querySelector("b")).toBeNull();
		expect(log.querySelector("img")).toBeNull();
	});

	it("marks stderr for screen readers as well as visually", () => {
		renderWithMantine(<LiveOutputView lines={[{ stream: "stderr", text: "boom" }]} running={false} label="Output" />);

		const line = screen.getByRole("log").querySelector('[data-stream="stderr"]');
		expect(line?.textContent).toBe("stderr: boom");
	});

	it("caps the rendered lines and says how many earlier lines are hidden and how many were skipped", () => {
		renderWithMantine(
			<LiveOutputView
				lines={stdout(liveOutputLineCap + 5)}
				hiddenLines={3}
				skippedLines={1}
				running={false}
				label="Output"
				testId="out"
			/>,
		);

		expect(screen.getByRole("log").querySelectorAll("[data-stream]")).toHaveLength(liveOutputLineCap);
		expect(screen.getByTestId("out-hidden").textContent).toBe("8 earlier lines hidden");
		expect(screen.getByTestId("out-skipped").textContent).toBe("1 line skipped");
	});

	it("follows new output while the reader is at the bottom and keeps a scrolled-up reader in place", () => {
		const { rerender } = renderWithMantine(<LiveOutputView lines={stdout(3)} running={true} label="Output" />);
		const viewport = screen.getByRole("log");
		const geometry = scrollGeometry(viewport, 1000, 200);

		rerender(withMantine(<LiveOutputView lines={stdout(4)} running={true} label="Output" />));
		expect(geometry.scrollTop).toBe(1000);

		geometry.scrollTop = 100;
		fireEvent.scroll(viewport);
		geometry.scrollHeight = 1200;
		rerender(withMantine(<LiveOutputView lines={stdout(5)} running={true} label="Output" />));
		expect(geometry.scrollTop).toBe(100);

		geometry.scrollTop = 1000;
		fireEvent.scroll(viewport);
		rerender(withMantine(<LiveOutputView lines={stdout(6)} running={true} label="Output" />));
		expect(geometry.scrollTop).toBe(1200);
	});

	it("shows the elapsed time and the time since the last output from the browser clock while running", () => {
		vi.useFakeTimers();
		vi.setSystemTime(new Date("2026-11-13T10:00:00Z"));
		const { rerender } = renderWithMantine(<LiveOutputView lines={[]} running={true} label="Output" testId="out" />);
		expect(screen.getByTestId("out-clock").textContent).toBe("Running for 0s, no output yet");

		act(() => vi.advanceTimersByTime(2000));
		rerender(withMantine(<LiveOutputView lines={stdout(1)} running={true} label="Output" testId="out" />));
		act(() => vi.advanceTimersByTime(3000));

		expect(screen.getByTestId("out-clock").textContent).toBe("Running for 5s, last output 3s ago");

		rerender(withMantine(<LiveOutputView lines={stdout(1)} running={false} label="Output" testId="out" />));
		expect(screen.queryByTestId("out-clock")).toBeNull();
	});
});
