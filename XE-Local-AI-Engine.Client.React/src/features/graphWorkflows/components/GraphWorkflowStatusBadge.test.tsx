// @vitest-environment jsdom

// Two closed vocabularies drive these pills, and both fail SILENTLY when they drift: a member with no colour renders
// an uncoloured badge, a member with no label renders its own wire token. Both are asserted here against the
// vocabulary arrays themselves, so adding a status without a colour or a German string is a red test rather than a
// pill an operator has to interpret.

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import {
	GraphWorkflowNodeStatusBadge,
	GraphWorkflowRunStatusBadge,
} from "@/features/graphWorkflows/components/GraphWorkflowStatusBadge";
import {
	type GraphWorkflowNodeRunStatus,
	type GraphWorkflowRunStatus,
	graphWorkflowNodeRunStatuses,
	graphWorkflowRunStatuses,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import en from "@/locales/en.json";
import { renderWithProviders } from "@/test/RenderWithProviders";

/** Mantine's Loader is the only progress indicator a pill can carry; the badge renders it as its left section. */
function hasSpinner(element: HTMLElement): boolean {
	return element.querySelector('[class*="Loader"]') !== null;
}

// The colour the operator is meant to read, per member. Mantine writes the resolved colour into the pill's inline
// custom properties, so this asserts the component's own map rather than restating it: a member the component left
// out would render no colour at all, and a member coloured differently fails on the name.
const expectedRunColors: Record<GraphWorkflowRunStatus, string> = {
	Pending: "gray",
	Running: "blue",
	WaitingForApproval: "orange",
	Cancelling: "orange",
	Completed: "green",
	Failed: "red",
	Cancelled: "gray",
};

const expectedNodeColors: Record<GraphWorkflowNodeRunStatus, string> = {
	Pending: "gray",
	Queued: "yellow",
	Running: "blue",
	WaitingForApproval: "orange",
	Succeeded: "green",
	Failed: "red",
	Skipped: "gray",
	Cancelled: "gray",
};

describe("GraphWorkflowStatusBadge", () => {
	afterEach(() => {
		cleanup();
	});

	it.each(graphWorkflowRunStatuses)("labels and colours the run status %s", (status) => {
		renderWithProviders(<GraphWorkflowRunStatusBadge status={status} data-testid="badge" />);

		const badge = screen.getByTestId("badge");
		expect(badge.textContent).toBe(en.pages.graphWorkflows.runStatus[status]);
		expect(badge.getAttribute("style")).toContain(`--mantine-color-${expectedRunColors[status]}-light`);
	});

	it.each(graphWorkflowNodeRunStatuses)("labels and colours the node status %s", (status) => {
		renderWithProviders(<GraphWorkflowNodeStatusBadge status={status} data-testid="badge" />);

		const badge = screen.getByTestId("badge");
		expect(badge.textContent).toBe(en.pages.graphWorkflows.nodeStatus[status]);
		expect(badge.getAttribute("style")).toContain(`--mantine-color-${expectedNodeColors[status]}-light`);
	});

	it("never animates a Queued node, and does animate a Running one", () => {
		// A queued node is waiting for a slot another node holds. A spinner there claims work is happening on it.
		renderWithProviders(<GraphWorkflowNodeStatusBadge status="Queued" data-testid="queued" />);
		renderWithProviders(<GraphWorkflowNodeStatusBadge status="Running" data-testid="running" />);

		expect(hasSpinner(screen.getByTestId("queued"))).toBe(false);
		expect(hasSpinner(screen.getByTestId("running"))).toBe(true);
	});

	it("leaves a run that is waiting for a decision still, and spins one that is cancelling", () => {
		renderWithProviders(<GraphWorkflowRunStatusBadge status="WaitingForApproval" data-testid="waiting" />);
		renderWithProviders(<GraphWorkflowRunStatusBadge status="Cancelling" data-testid="cancelling" />);

		expect(hasSpinner(screen.getByTestId("waiting"))).toBe(false);
		expect(hasSpinner(screen.getByTestId("cancelling"))).toBe(true);
	});

	it("reads an unknown or absent status as the inert one rather than inventing a state", () => {
		renderWithProviders(<GraphWorkflowRunStatusBadge status="Exploded" data-testid="run" />);
		renderWithProviders(<GraphWorkflowNodeStatusBadge status={undefined} data-testid="node" />);

		expect(screen.getByTestId("run").textContent).toBe(en.pages.graphWorkflows.runStatus.Pending);
		expect(screen.getByTestId("node").textContent).toBe(en.pages.graphWorkflows.nodeStatus.Pending);
	});
});
