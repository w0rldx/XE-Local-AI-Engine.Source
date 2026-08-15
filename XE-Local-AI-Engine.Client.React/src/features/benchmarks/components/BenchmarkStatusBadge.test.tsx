// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import type { BenchmarkJudgeStatus, BenchmarkPrimaryStatus } from "@/features/benchmarks/models/BenchmarkModels";
import { BenchmarkStatusBadge } from "@/features/benchmarks/components/BenchmarkStatusBadge";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The badge is the only place a benchmark status colour is decided, and the primary and judge lifecycles are
// independent unions rendered by the same component — so every value in either one must resolve to a colour and to a
// non-empty accessible label, with no untranslated key leaking through.

const primaryStatuses: BenchmarkPrimaryStatus[] = ["Queued", "Running", "CancelRequested", "Succeeded", "Failed", "Cancelled"];
const judgeStatuses: BenchmarkJudgeStatus[] = [
	"Disabled",
	"Pending",
	"Skipped",
	"Queued",
	"Running",
	"Succeeded",
	"Failed",
	"Cancelled",
];

/** The single rendered badge element. */
function badge(container: HTMLElement): HTMLElement {
	const element = container.querySelector("[aria-label]");
	expect(element).not.toBeNull();
	return element as HTMLElement;
}

describe("BenchmarkStatusBadge", () => {
	afterEach(cleanup);

	it.each([...new Set([...primaryStatuses, ...judgeStatuses])])("labels the %s badge for assistive tech", (status) => {
		const { container } = renderWithProviders(<BenchmarkStatusBadge status={status} />);
		const element = badge(container);

		// The visible text and the accessible name are the same string, and neither is a raw i18n key.
		expect(element.textContent?.trim()).not.toBe("");
		expect(element.getAttribute("aria-label")).toBe(element.textContent?.trim());
		expect(element.textContent).not.toContain("pages.benchmarks.status.");
	});

	// The one status whose label is genuinely not its enum name — a missing translation would silently show
	// "CancelRequested" instead.
	it("renders the localized label for CancelRequested", () => {
		renderWithProviders(<BenchmarkStatusBadge status="CancelRequested" />);

		expect(screen.getByText("Cancellation requested")).toBeTruthy();
	});

	// Success and failure must never share a colour — it is the one distinction an operator reads at a glance.
	it("colours success and failure differently", () => {
		const succeeded = renderWithProviders(<BenchmarkStatusBadge status="Succeeded" />);
		const succeededStyle = badge(succeeded.container).getAttribute("style");
		succeeded.unmount();

		const failed = renderWithProviders(<BenchmarkStatusBadge status="Failed" />);

		expect(badge(failed.container).getAttribute("style")).not.toBe(succeededStyle);
	});
});
