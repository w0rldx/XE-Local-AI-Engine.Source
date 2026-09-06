// @vitest-environment jsdom

// The hybrid error shape is only worth having if the two halves render differently, so that is what this pins: a keyed
// issue is a control that takes the operator to its node or edge, an unkeyed one is a sentence in a single Alert, and
// both arrive through the same `GraphWorkflowGraphIssue` whether the client or the server raised them.

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { GraphWorkflowValidationStrip } from "@/features/graphWorkflows/components/GraphWorkflowValidationStrip";
import { renderWithProviders } from "@/test/RenderWithProviders";

afterEach(cleanup);

describe("GraphWorkflowValidationStrip", () => {
	it("renders nothing at all when the graph is clean", () => {
		renderWithProviders(<GraphWorkflowValidationStrip issues={[]} onSelectSubject={vi.fn()} />);

		expect(screen.queryByTestId("graph-workflow-validation-strip")).toBeNull();
	});

	it("attaches a keyed client issue to its subject and selects it on click", () => {
		const onSelectSubject = vi.fn();
		renderWithProviders(
			<GraphWorkflowValidationStrip issues={[{ rule: "unreachable", subject: "lookup" }]} onSelectSubject={onSelectSubject} />,
		);

		const chip = screen.getByTestId("graph-workflow-validation-issue-lookup");
		// The subject is interpolated, so the operator reads which node rather than which rule.
		expect(chip.textContent).toContain("lookup");
		expect(screen.queryByTestId("graph-workflow-validation-unkeyed")).toBeNull();

		fireEvent.click(chip);
		expect(onSelectSubject).toHaveBeenCalledWith("lookup");
	});

	it("carries the server's own sentence through the same path as a client rule", () => {
		const onSelectSubject = vi.fn();
		renderWithProviders(
			<GraphWorkflowValidationStrip
				issues={[{ rule: "serverRejected", subject: "e3", message: "edge e3 names a tool that is not allowed" }]}
				onSelectSubject={onSelectSubject}
			/>,
		);

		const chip = screen.getByTestId("graph-workflow-validation-issue-e3");
		expect(chip.textContent).toBe("edge e3 names a tool that is not allowed");

		fireEvent.click(chip);
		expect(onSelectSubject).toHaveBeenCalledWith("e3");
	});

	it("scrolls a long issue list instead of growing over the canvas", () => {
		renderWithProviders(
			<GraphWorkflowValidationStrip
				issues={Array.from({ length: 15 }, (_unused, index) => ({ rule: "unreachable" as const, subject: `n${index}` }))}
				onSelectSubject={vi.fn()}
			/>,
		);

		const chips = screen.getByTestId("graph-workflow-validation-issues");
		expect(chips.style.maxHeight).toBe("120px");
		expect(chips.style.overflowY).toBe("auto");
		// Without this the strip takes a share of the height-constrained editor column and covers the canvas controls.
		expect(chips.style.flex).toBe("0 0 auto");
	});

	it("keeps one chip per subject when two server errors read the same", () => {
		// De-duplicating on the sentence would hide one of two bad cards behind the other.
		renderWithProviders(
			<GraphWorkflowValidationStrip
				issues={[
					{ rule: "serverRejected", subject: "analyze", message: "this node names a tool that is not allowed" },
					{ rule: "serverRejected", subject: "lookup", message: "this node names a tool that is not allowed" },
				]}
				onSelectSubject={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("graph-workflow-validation-issue-analyze")).toBeTruthy();
		expect(screen.getByTestId("graph-workflow-validation-issue-lookup")).toBeTruthy();
	});

	it("renders an unkeyed issue once above the keyed ones, and de-duplicates a repeat", () => {
		renderWithProviders(
			<GraphWorkflowValidationStrip
				issues={[
					{ rule: "noStart" },
					{ rule: "noStart" },
					{ rule: "serverRejected", message: "the graph has no Start node" },
					{ rule: "duplicateNodeKey", subject: "analyze" },
				]}
				onSelectSubject={vi.fn()}
			/>,
		);

		const alert = screen.getByTestId("graph-workflow-validation-unkeyed");
		const items = alert.querySelectorAll("li");
		// Two identical `noStart` issues are one problem; the server's differently-worded one is a second line.
		expect(items).toHaveLength(2);
		expect(alert.textContent).toContain("the graph has no Start node");
		expect(screen.getByTestId("graph-workflow-validation-issue-analyze")).toBeTruthy();
	});
});
