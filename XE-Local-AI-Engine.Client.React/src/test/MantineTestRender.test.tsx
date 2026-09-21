// @vitest-environment jsdom

import { Collapse, Drawer, Modal } from "@mantine/core";
import { fireEvent, screen } from "@testing-library/react";
import { act, useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { renderWithProviders } from "@/test/RenderWithProviders";

// Why this file exists: Mantine's `env="test"` only short-circuits what <Transition> RENDERS — `useTransition` runs
// above that check regardless, so an overlay whose default duration is non-zero schedules a real rAF + setTimeout on
// every close and keeps it alive until the component hosting the Transition unmounts (an overlay never does; only its
// `mounted` prop toggles). Under load that timer fires AFTER `afterEach(cleanup)` has torn the tree down, and vitest
// exits 1 with every test in the file green — a failure no assertion in the failing file can catch.
// `testMantineTheme`, applied by the wrappers in this folder, zeroes those durations so `handleStateChange` takes its
// synchronous branch and schedules nothing at all.
//
// Fake timers are what make "nothing is scheduled" observable; `vi.getTimerCount()` counts rAF as well as setTimeout,
// so it sees both halves of the async branch. Drop `theme={testMantineTheme}` from `renderWithProviders` and the
// assertion after the close reports 3 pending timers instead of 0 — that is the break proof.

/**
 * Long enough for the short-lived timers an overlay schedules for reasons of its own — Mantine's `useFocusReturn`
 * restores focus on a 10 ms timeout — and far short of the transition durations this theme zeroes (Tooltip 100,
 * Popover 150, Modal 200, Drawer 250). A transition timer is therefore still pending at the end of this window.
 */
const settleWindowMs = 20;

afterEach(() => {
	vi.useRealTimers();
});

/** Starts open and closes on click, so the `mounted` prop toggles exactly the way a real dialog's does. */
function OverlayProbe({ kind }: { kind: "modal" | "drawer" }) {
	const [opened, setOpened] = useState(true);
	const close = () => {
		setOpened(false);
	};
	const body = <span>probe-body</span>;

	return (
		<>
			<button type="button" onClick={close}>
				close-probe
			</button>
			{kind === "modal" ? (
				<Modal opened={opened} onClose={close} title="probe">
					{body}
				</Modal>
			) : (
				<Drawer opened={opened} onClose={close} title="probe">
					{body}
				</Drawer>
			)}
		</>
	);
}

/** Starts expanded and collapses on click, the shape every disclosure in this app renders. */
function CollapseProbe() {
	const [expanded, setExpanded] = useState(true);

	return (
		<>
			<button
				type="button"
				onClick={() => {
					setExpanded((open) => !open);
				}}
			>
				toggle-probe
			</button>
			<Collapse expanded={expanded}>
				<span>collapse-body</span>
			</Collapse>
		</>
	);
}

describe("testMantineTheme", () => {
	it.each(["modal", "drawer"] as const)("leaves no pending transition timer after a %s closes", (kind) => {
		vi.useFakeTimers();
		renderWithProviders(<OverlayProbe kind={kind} />);
		expect(screen.getByText("probe-body")).not.toBeNull();

		// Whatever opening scheduled drains here, so the count after the close is about the close alone.
		act(() => {
			vi.advanceTimersByTime(settleWindowMs);
		});
		expect(vi.getTimerCount()).toBe(0);

		fireEvent.click(screen.getByText("close-probe"));
		expect(screen.queryByText("probe-body")).toBeNull();

		act(() => {
			vi.advanceTimersByTime(settleWindowMs);
		});
		expect(vi.getTimerCount()).toBe(0);
	});

	// Collapse is the overlay the theme deliberately does NOT list, because zeroing its duration would not help:
	// `useCollapse` schedules its frame above its own duration check. What makes it safe instead is the guard on
	// that frame — it returns unless the collapsing element is still mounted — so this pins the safety, not a
	// duration. A Mantine release that dropped the guard, or that scheduled a timeout too, fails here.
	it("lets a Collapse toggle settle, and its pending frame does nothing once the tree is gone", () => {
		vi.useFakeTimers();
		const view = renderWithProviders(<CollapseProbe />);

		fireEvent.click(screen.getByText("toggle-probe"));
		act(() => {
			vi.advanceTimersByTime(settleWindowMs);
		});
		expect(vi.getTimerCount()).toBe(0);

		fireEvent.click(screen.getByText("toggle-probe"));
		expect(vi.getTimerCount()).toBe(1);
		view.unmount();

		expect(() =>
			act(() => {
				vi.advanceTimersByTime(settleWindowMs);
			}),
		).not.toThrow();
		expect(vi.getTimerCount()).toBe(0);
	});
});
