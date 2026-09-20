// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { DevelopmentEventTimeline } from "@/features/development/components/DevelopmentEventTimeline";
import type { DevelopmentEvent } from "@/features/development/models/DevelopmentModels";
import en from "@/locales/en.json";

// Resolves against the REAL en.json rather than echoing the fallback the way the sibling component tests do: the
// whole claim under test is that the key exists and wins over the raw backend token, and a t() that always answers
// with its fallback would pass with no labels in the locale file at all.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, fallback?: string) => {
			const resolved = key.split(".").reduce<unknown>(
				(acc, segment) => {
					if (acc === undefined || acc === null || typeof acc !== "object") {
						return undefined;
					}
					return (acc as Record<string, unknown>)[segment];
				},
				en as Record<string, unknown>,
			);
			return typeof resolved === "string" ? resolved : (fallback ?? key);
		},
	}),
}));

afterEach(cleanup);
beforeEach(() => {
	globalThis.ResizeObserver = class {
		disconnect = vi.fn();
		observe = vi.fn();
		unobserve = vi.fn();
	};
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })),
	});
});

// Built from the generated response type without a cast — which is the point: the test stops compiling when the
// model gains a member, instead of silently constructing a shape the app never sees. The feed carries an explicit
// null for every member an event does not use.
function event(id: string, sequence: number, outcome: string | null, operationPhase: string | null = null): DevelopmentEvent {
	return {
		id,
		projectId: "project-1",
		taskId: null,
		attemptId: null,
		sequence,
		eventType: "TaskTransitioned",
		occurredAtUtc: 1700 + sequence,
		operationId: null,
		operationPhase,
		outcome,
	};
}

function renderTimeline(events: readonly DevelopmentEvent[]) {
	render(
		<MantineProvider env="test">
			<DevelopmentEventTimeline events={events} untiedEvents={[]} onRefresh={vi.fn()} />
		</MantineProvider>,
	);
}

describe("DevelopmentEventTimeline", () => {
	it("labels an operator-directed transition instead of showing the backend token", () => {
		renderTimeline([event("event-1", 1, "TransitionedByOperator")]);

		expect(screen.getByText("Changed by operator")).toBeTruthy();
		expect(screen.queryByText("TransitionedByOperator")).toBeNull();
	});

	// Only tokens whose label DIFFERS from the token can carry weight here: the mocked `t` falls back to the raw
	// value, so asserting on `Failed` or `Passed` (labelled as themselves) would pass with the locale block deleted.
	it("labels the gate's rejection verdict", () => {
		renderTimeline([event("event-1", 1, "ChangesRequested")]);

		expect(screen.getByText("Changes requested")).toBeTruthy();
		expect(screen.queryByText("ChangesRequested")).toBeNull();
	});

	it("labels the event type beside the outcome", () => {
		renderTimeline([event("event-1", 1, "TransitionedByOperator")]);

		expect(screen.getByText(/Task transitioned/)).toBeTruthy();
		expect(screen.queryByText(/TaskTransitioned/)).toBeNull();
	});

	it("falls back to the raw token a newer server invents rather than rendering the key", () => {
		renderTimeline([event("event-1", 1, "SomethingNewer")]);

		expect(screen.getByText("SomethingNewer")).toBeTruthy();
	});

	it("keeps showing the operation phase when a row carries no outcome", () => {
		renderTimeline([event("event-1", 1, null, "Completed")]);

		expect(screen.getByText("Completed")).toBeTruthy();
	});
});
