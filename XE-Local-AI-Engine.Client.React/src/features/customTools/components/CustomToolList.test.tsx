// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { CustomToolList } from "@/features/customTools/components/CustomToolList";
import type { CustomToolView } from "@/features/customTools/models/CustomToolModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Pure presentation: the parent owns the data and the handlers. What matters here is that the host-exec risk stays
// visible in the list (the danger badge on an ENABLED tool), that the model-facing `custom__` name is always shown
// prefixed, and that row actions are blocked while a mutation is in flight.

function tool(overrides: Partial<CustomToolView> = {}): CustomToolView {
	return {
		id: "tool-1",
		name: "custom__fetch_status",
		description: "Fetches a status page.",
		kind: "HttpFetch",
		mode: "Fixed",
		enabled: false,
		acknowledged: true,
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		parameters: [],
		http: { method: "GET", urlTemplate: "https://example.test", headers: [], bodyTemplate: "", allowedHosts: [] },
		command: null,
		...overrides,
	};
}

describe("CustomToolList", () => {
	afterEach(cleanup);

	it("shows the empty state instead of a table when there are no tools", () => {
		renderWithProviders(<CustomToolList tools={[]} isMutating={false} onEdit={vi.fn()} onDelete={vi.fn()} />);

		expect(screen.getByTestId("custom-tools-empty")).toBeTruthy();
		expect(screen.queryByTestId("custom-tools-table")).toBeNull();
	});

	it("renders one row per tool with its version", () => {
		renderWithProviders(
			<CustomToolList
				tools={[tool(), tool({ id: "tool-2", name: "custom__run_script", kind: "Command", version: 1 })]}
				isMutating={false}
				onEdit={vi.fn()}
				onDelete={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("custom-tool-row-tool-1")).toBeTruthy();
		expect(screen.getByTestId("custom-tool-row-tool-2")).toBeTruthy();
		expect(screen.getByText("custom__fetch_status")).toBeTruthy();
	});

	// The name the model sees always carries the reserved prefix, whether or not the stored value already has it.
	it("displays a bare stored name with the reserved prefix", () => {
		renderWithProviders(
			<CustomToolList tools={[tool({ name: "fetch_status" })]} isMutating={false} onEdit={vi.fn()} onDelete={vi.fn()} />,
		);

		expect(screen.getByText("custom__fetch_status")).toBeTruthy();
	});

	// The danger badge is the list's only standing reminder that an enabled tool can run on the host.
	it("flags only an enabled tool with the host-access badge", () => {
		renderWithProviders(
			<CustomToolList
				tools={[tool({ enabled: true }), tool({ id: "tool-2", enabled: false })]}
				isMutating={false}
				onEdit={vi.fn()}
				onDelete={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("custom-tool-danger-tool-1")).toBeTruthy();
		expect(screen.queryByTestId("custom-tool-danger-tool-2")).toBeNull();
	});

	it("forwards the id on edit and the whole tool on delete", () => {
		const onEdit = vi.fn();
		const onDelete = vi.fn();
		const row = tool();
		renderWithProviders(<CustomToolList tools={[row]} isMutating={false} onEdit={onEdit} onDelete={onDelete} />);

		fireEvent.click(screen.getByTestId("custom-tool-edit-tool-1"));
		fireEvent.click(screen.getByTestId("custom-tool-delete-tool-1"));

		expect(onEdit).toHaveBeenCalledExactlyOnceWith("tool-1");
		expect(onDelete).toHaveBeenCalledExactlyOnceWith(row);
	});

	it("disables the row actions while a mutation is in flight", () => {
		renderWithProviders(<CustomToolList tools={[tool()]} isMutating={true} onEdit={vi.fn()} onDelete={vi.fn()} />);

		expect((screen.getByTestId("custom-tool-edit-tool-1") as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByTestId("custom-tool-delete-tool-1") as HTMLButtonElement).disabled).toBe(true);
	});
});
