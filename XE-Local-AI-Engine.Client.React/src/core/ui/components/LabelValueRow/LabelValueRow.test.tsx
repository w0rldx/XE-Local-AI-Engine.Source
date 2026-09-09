// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { LabelValueRow } from "@/core/ui/components/LabelValueRow/LabelValueRow";
import { installJsdomEnvironmentMocks, renderWithMantine as renderWithProviders } from "@/test/MantineTestRender";

describe("LabelValueRow", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders the label and its value", () => {
		renderWithProviders(<LabelValueRow label="Seed">42</LabelValueRow>);

		expect(screen.getByText("Seed")).toBeTruthy();
		expect(screen.getByText("42")).toBeTruthy();
	});

	it("leaves the label unsized by default, so the value follows it directly", () => {
		renderWithProviders(<LabelValueRow label="Request id">abc</LabelValueRow>);

		expect(screen.getByText("Request id").style.width).toBe("");
	});

	it("pins the label to a fixed column width when one is supplied", () => {
		renderWithProviders(
			<LabelValueRow label="Request id" labelWidth={160}>
				abc
			</LabelValueRow>,
		);

		// Mantine rewrites a numeric width to its scaled rem form, so assert the pinning, not the literal px.
		expect(screen.getByText("Request id").style.width).not.toBe("");
	});

	it("pushes the value to the far edge and right-aligns it in the space-between layout", () => {
		renderWithProviders(
			<LabelValueRow label="Chunks" justify="space-between">
				12
			</LabelValueRow>,
		);

		const value = screen.getByText("12");
		expect(value.style.textAlign).toBe("right");
		expect((value.parentElement as HTMLElement).style.getPropertyValue("--group-justify")).toBe("space-between");
	});

	it("renders the value unwrapped in the default layout, so a caller's own element keeps its styling", () => {
		renderWithProviders(
			<LabelValueRow label="Status">
				<span data-testid="status-badge">Succeeded</span>
			</LabelValueRow>,
		);

		const badge = screen.getByTestId("status-badge");
		expect((badge.parentElement as HTMLElement).getAttribute("data-testid")).toBeNull();
		expect(badge.previousElementSibling?.textContent).toBe("Status");
	});

	it("centres the label against its value with a small gap by default", () => {
		renderWithProviders(<LabelValueRow label="Status">Succeeded</LabelValueRow>);

		const group = screen.getByText("Status").parentElement as HTMLElement;
		expect(group.style.getPropertyValue("--group-align")).toBe("center");
		expect(group.style.getPropertyValue("--group-gap")).toBe("var(--mantine-spacing-sm)");
	});

	it("takes the alignment and gap from the caller, so a wrapping value can pin its label to the first line", () => {
		renderWithProviders(
			<LabelValueRow label="Prompt" align="flex-start" gap="md">
				a very long prompt
			</LabelValueRow>,
		);

		const group = screen.getByText("Prompt").parentElement as HTMLElement;
		expect(group.style.getPropertyValue("--group-align")).toBe("flex-start");
		expect(group.style.getPropertyValue("--group-gap")).toBe("var(--mantine-spacing-md)");
	});

	it("scales the label to the surrounding content when a size is given", () => {
		renderWithProviders(
			<LabelValueRow label="Prompt" size="xs">
				a cat
			</LabelValueRow>,
		);

		expect(screen.getByText("Prompt").style.getPropertyValue("--text-fz")).toBe("var(--mantine-font-size-xs)");
	});
});
