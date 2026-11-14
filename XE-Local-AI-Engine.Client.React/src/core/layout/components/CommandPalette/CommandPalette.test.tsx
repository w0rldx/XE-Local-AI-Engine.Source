// @vitest-environment jsdom

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { useRouterState } from "@tanstack/react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { generatedMock } = vi.hoisted(() => ({
	generatedMock: {
		getNodeSettingsOptions: vi.fn(),
		getGraphWorkflowCapabilityOptions: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>();
	return {
		...actual,
		getNodeSettingsOptions: generatedMock.getNodeSettingsOptions,
		getGraphWorkflowCapabilityOptions: generatedMock.getGraphWorkflowCapabilityOptions,
	};
});

import { CommandPalette } from "@/core/layout/components/CommandPalette/CommandPalette";
import { renderWithProviders } from "@/test/RenderWithProviders";

function PathProbe() {
	const pathname = useRouterState({ select: (state) => state.location.pathname });
	return <output data-testid="path">{pathname}</output>;
}

function renderPalette(settings: Record<string, unknown> = {}) {
	generatedMock.getNodeSettingsOptions.mockReturnValue({ queryKey: ["getNodeSettings"], queryFn: async () => settings });
	generatedMock.getGraphWorkflowCapabilityOptions.mockReturnValue({
		queryKey: ["getGraphWorkflowCapability"],
		queryFn: async () => ({ enabled: true }),
	});
	return renderWithProviders(
		<>
			<CommandPalette />
			<PathProbe />
			<textarea data-testid="composer" />
			<div className="monaco-editor">
				<textarea data-testid="code-editor" />
			</div>
		</>,
		{ route: "/" },
	);
}

const pressModK = (target: Element = document.body): boolean =>
	fireEvent.keyDown(target, { key: "k", code: "KeyK", ctrlKey: true });

const optionLabels = (): string[] =>
	screen.getAllByRole("option").map((option) => option.querySelector("span")?.textContent ?? "");

describe("CommandPalette", () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	it("opens on Ctrl+K, closes on Escape and toggles closed on a second Ctrl+K", async () => {
		renderPalette();
		await screen.findByTestId("path");

		expect(pressModK()).toBe(false);
		const input = await screen.findByRole("combobox", { name: "Page name" });
		expect(screen.getByTestId("command-palette")).toBeTruthy();

		fireEvent.keyDown(input, { key: "Escape" });
		await waitFor(() => expect(screen.queryByRole("combobox", { name: "Page name" })).toBeNull());

		pressModK();
		const reopened = await screen.findByRole("combobox", { name: "Page name" });
		pressModK(reopened);
		await waitFor(() => expect(screen.queryByRole("combobox", { name: "Page name" })).toBeNull());
	});

	it("filters by every typed word, moves with the arrow keys and opens the active page on Enter", async () => {
		renderPalette();
		await screen.findByTestId("path");
		pressModK();
		const input = await screen.findByRole("combobox", { name: "Page name" });

		fireEvent.change(input, { target: { value: "s" } });
		const options = screen.getAllByRole("option");
		expect(options.length).toBeGreaterThan(1);
		expect(input.getAttribute("aria-activedescendant")).toBe(options[0]?.id);
		fireEvent.keyDown(input, { key: "ArrowDown" });
		expect(options[1]?.getAttribute("aria-selected")).toBe("true");
		expect(input.getAttribute("aria-activedescendant")).toBe(options[1]?.id);
		fireEvent.keyDown(input, { key: "ArrowUp" });
		fireEvent.keyDown(input, { key: "ArrowUp" });
		expect(input.getAttribute("aria-activedescendant")).toBe(options.at(-1)?.id);

		// Word order does not matter.
		fireEvent.change(input, { target: { value: "settings node" } });
		expect(optionLabels()).toEqual(["Node Settings"]);
		fireEvent.keyDown(input, { key: "Enter" });

		await waitFor(() => expect(screen.getByTestId("path").textContent).toBe("/node-settings"));
		expect(screen.queryByRole("combobox", { name: "Page name" })).toBeNull();
	});

	it("says so when no page matches", async () => {
		renderPalette();
		await screen.findByTestId("path");
		pressModK();
		const input = await screen.findByRole("combobox", { name: "Page name" });

		fireEvent.change(input, { target: { value: "no such page" } });

		expect(screen.getByTestId("command-palette-empty").textContent).toBe("No page matches.");
		expect(screen.queryAllByRole("option")).toHaveLength(0);
	});

	it("lists only what the navigation shows: no Advanced-only page in Simple mode, no page whose feature is off", async () => {
		renderPalette({ uiMode: "simple" });
		await screen.findByTestId("path");
		pressModK();
		await screen.findByRole("combobox", { name: "Page name" });
		await waitFor(() => expect(optionLabels()).not.toContain("Diagnostics"));
		expect(optionLabels()).toContain("Node Settings");
	});

	it("drops a page whose feature switch is off", async () => {
		renderPalette({ schedulerEnabled: false });
		await screen.findByTestId("path");
		pressModK();
		await screen.findByRole("combobox", { name: "Page name" });
		await waitFor(() => expect(optionLabels()).not.toContain("Scheduler"));
		expect(optionLabels()).toContain("Diagnostics");
	});

	it("opens from a text field but leaves Ctrl+K to a code editor", async () => {
		renderPalette();
		await screen.findByTestId("path");

		expect(pressModK(screen.getByTestId("code-editor"))).toBe(true);
		expect(screen.queryByRole("combobox", { name: "Page name" })).toBeNull();

		pressModK(screen.getByTestId("composer"));
		expect(await screen.findByRole("combobox", { name: "Page name" })).toBeTruthy();
	});
});
