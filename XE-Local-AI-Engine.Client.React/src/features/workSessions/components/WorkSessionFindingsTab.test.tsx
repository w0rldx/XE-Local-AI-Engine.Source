// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { WorkSessionFindingsTab } from "@/features/workSessions/components/WorkSessionFindingsTab";
import type { WorkSessionFindingResponse } from "@/features/workSessions/models/WorkSessionModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

function finding(id: string, overrides: Partial<WorkSessionFindingResponse> = {}): WorkSessionFindingResponse {
	return {
		id,
		taskId: null,
		sequence: 1,
		kind: "Finding",
		text: `text ${id}`,
		sourceRef: null,
		createdStep: 1,
		superseded: false,
		...overrides,
	};
}

describe("WorkSessionFindingsTab", () => {
	afterEach(() => {
		cleanup();
	});

	it("groups by kind and omits a group with nothing in it", () => {
		renderWithProviders(
			<WorkSessionFindingsTab
				findings={[
					finding("f1", { kind: "Finding" }),
					finding("f2", { kind: "Decision" }),
					finding("f3", { kind: "OpenQuestion" }),
				]}
			/>,
		);

		expect(screen.getByTestId("work-session-findings-group-Finding")).toBeDefined();
		expect(screen.getByTestId("work-session-findings-group-Decision")).toBeDefined();
		expect(screen.getByTestId("work-session-findings-group-OpenQuestion")).toBeDefined();
		expect(screen.queryByTestId("work-session-findings-group-Evidence")).toBeNull();
	});

	it("hides superseded findings until the switch is turned on", () => {
		renderWithProviders(<WorkSessionFindingsTab findings={[finding("f1"), finding("f2", { superseded: true })]} />);

		expect(screen.getByTestId("work-session-finding-f1")).toBeDefined();
		expect(screen.queryByTestId("work-session-finding-f2")).toBeNull();

		fireEvent.click(screen.getByTestId("work-session-findings-show-superseded"));

		expect(screen.getByTestId("work-session-finding-f2").getAttribute("data-superseded")).toBe("true");
	});

	it("renders the source reference beside the finding it came from", () => {
		renderWithProviders(<WorkSessionFindingsTab findings={[finding("f1", { sourceRef: "kb://doc-42#3" })]} />);

		expect(screen.getByTestId("work-session-finding-source-f1").textContent).toBe("kb://doc-42#3");
	});

	it("says so when the agent has recorded nothing", () => {
		renderWithProviders(<WorkSessionFindingsTab findings={[]} />);

		expect(screen.getByTestId("work-session-findings-empty")).toBeDefined();
	});
});
