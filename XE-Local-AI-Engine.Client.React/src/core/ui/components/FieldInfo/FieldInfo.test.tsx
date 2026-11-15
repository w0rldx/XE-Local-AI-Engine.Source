// @vitest-environment jsdom

import { NumberInput, Switch } from "@mantine/core";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { FieldInfo, FieldWithInfo } from "@/core/ui/components/FieldInfo/FieldInfo";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

const TEXT = "Keeps the most likely tokens.\n\nLower it for focused answers.";

describe("FieldInfo", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("opens a read-only card on hover and closes it when the pointer leaves", async () => {
		renderWithMantine(<FieldInfo label="Top P" text={TEXT} testId="info" />);

		fireEvent.mouseEnter(screen.getByTestId("info"));
		const card = await screen.findByRole("tooltip");
		// One paragraph per blank-line block, under the field name as the title.
		expect([...card.querySelectorAll("p")].map((p) => p.textContent)).toEqual([
			"Top P",
			"Keeps the most likely tokens.",
			"Lower it for focused answers.",
		]);
		// Not interactive: Mantine lets only an interactive tooltip take the pointer.
		expect(card.hasAttribute("data-interactive")).toBe(false);
		fireEvent.mouseLeave(screen.getByTestId("info"));
		await waitFor(() => expect(screen.queryByRole("tooltip")).toBeNull());
	});

	it("opens on keyboard focus of its named button", async () => {
		renderWithMantine(<FieldInfo label="Top P" text={TEXT} testId="info" />);

		const button = screen.getByRole("button", { name: "About Top P" });
		fireEvent.focus(button);
		const card = await screen.findByRole("tooltip");
		expect(card.textContent).toContain("Keeps the most likely tokens.");
		expect(button.getAttribute("aria-describedby")).toBe(card.id);
		fireEvent.blur(button);
		await waitFor(() => expect(screen.queryByRole("tooltip")).toBeNull());
	});

	it("layers the card above the dialog it sits in", async () => {
		renderWithMantine(
			<DialogShell opened={true} onClose={vi.fn()} title="Sampling">
				<FieldInfo label="Top P" text={TEXT} testId="info" />
			</DialogShell>,
		);

		fireEvent.focus(await screen.findByTestId("info"));
		const card = await screen.findByRole("tooltip");
		expect(Number(card.style.zIndex)).toBeGreaterThan(400);
	});
});

describe("FieldWithInfo", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("keeps the input's accessible name and does not focus the input when the icon is clicked", () => {
		renderWithMantine(
			<FieldWithInfo label="Top P" text={TEXT} testId="info">
				<NumberInput label="Top P" />
			</FieldWithInfo>,
		);

		const input = screen.getByRole("textbox", { name: "Top P" });
		const label = screen.getByText("Top P", { selector: "label" });
		expect(label.contains(screen.getByTestId("info"))).toBe(false);
		fireEvent.click(screen.getByTestId("info"));
		expect(document.activeElement).not.toBe(input);
	});

	it("does not toggle a Switch when the icon is clicked", () => {
		renderWithMantine(
			<FieldWithInfo label="Auto compaction" text={TEXT} testId="info">
				<Switch label="Auto compaction" />
			</FieldWithInfo>,
		);

		const toggle = screen.getByRole("switch", { name: "Auto compaction" });
		fireEvent.click(screen.getByTestId("info"));
		expect(toggle).toHaveProperty("checked", false);
	});

	it("renders the input alone without a text", () => {
		renderWithMantine(
			<FieldWithInfo label="Top P" testId="info">
				<NumberInput label="Top P" />
			</FieldWithInfo>,
		);

		expect(screen.getByRole("textbox", { name: "Top P" })).toBeDefined();
		expect(screen.queryByTestId("info")).toBeNull();
	});
});
