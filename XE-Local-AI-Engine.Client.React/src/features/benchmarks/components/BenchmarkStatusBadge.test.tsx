// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { BenchmarkJudgeStateBadge, BenchmarkStatusBadge } from "@/features/benchmarks/components/BenchmarkStatusBadge";
import type { BenchmarkJudgeState, BenchmarkPrimaryStatus } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The badges are the only place a benchmark status colour is decided, and the primary and judge lifecycles are
// separate unions with separate vocabularies — so every value in either one must resolve to a colour and to a
// non-empty accessible label, with no untranslated key leaking through.

const primaryStatuses: BenchmarkPrimaryStatus[] = ["Queued", "Running", "CancelRequested", "Succeeded", "Failed", "Cancelled"];
const judgeStates: BenchmarkJudgeState[] = ["none", "queued", "running", "succeeded", "failed", "cancelled"];

function badge(container: HTMLElement): HTMLElement {
	const element = container.querySelector("[aria-label]");
	// biome-ignore lint/suspicious/noMisplacedAssertion: guard inside a helper every caller runs from within a test — it fails the caller, not module load.
	expect(element).not.toBeNull();
	return element as HTMLElement;
}

describe("BenchmarkStatusBadge", () => {
	afterEach(cleanup);

	it.each(primaryStatuses)("labels the %s badge for assistive tech", (status) => {
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

describe("BenchmarkJudgeStateBadge", () => {
	afterEach(cleanup);

	it.each(judgeStates)("labels the %s judging for assistive tech", (state) => {
		const { container } = renderWithProviders(<BenchmarkJudgeStateBadge state={state} />);
		const element = badge(container);

		expect(element.textContent?.trim()).not.toBe("");
		expect(element.getAttribute("aria-label")).toBe(element.textContent?.trim());
		expect(element.textContent).not.toContain("pages.benchmarks.judgeState.");
	});

	// "never judged" and "judged, and it failed" are different facts and must not read the same.
	it("distinguishes an unjudged run from a failed judging", () => {
		const unjudged = renderWithProviders(<BenchmarkJudgeStateBadge state="none" />);
		const unjudgedText = badge(unjudged.container).textContent;
		unjudged.unmount();

		const failed = renderWithProviders(<BenchmarkJudgeStateBadge state="failed" />);

		expect(badge(failed.container).textContent).not.toBe(unjudgedText);
	});
});
