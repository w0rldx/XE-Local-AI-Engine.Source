// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { GgufAcquisitionStatus } from "@/core/model-downloads/models/GgufAcquisitionModels";
import { ActiveOperationsIndicator } from "@/core/operations/components/ActiveOperationsIndicator";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The source feeds are hub-backed hooks with their own tests; the merge and normalization between them and the chip
// run for real.
const sources = vi.hoisted(() => ({
	gguf: new Map<string, unknown>(),
	runtime: undefined as unknown,
}));

vi.mock("@/core/model-downloads/queries/useGgufAcquisitions", () => ({
	useActiveGgufAcquisitions: () => sources.gguf,
}));
vi.mock("@/core/runtime-acquisition/hooks/useRuntimeAcquisitionHub", () => ({
	useRuntimeAcquisitionHub: () => sources.runtime,
}));

function downloads(...entries: Partial<GgufAcquisitionStatus>[]) {
	sources.gguf = new Map(
		entries.map((entry, index) => [
			`op-${index}`,
			{
				operationId: `op-${index}`,
				operationKind: "Download",
				modelName: `model-${index}`,
				phase: "Downloading",
				pct: undefined,
				completedBytes: null,
				totalBytes: null,
				startedAtUtc: null,
				updatedAtUtc: null,
				errorCode: null,
				sanitizedMessage: null,
				...entry,
			},
		]),
	);
}

beforeEach(() => {
	sources.gguf = new Map();
	sources.runtime = undefined;
});

afterEach(cleanup);

describe("ActiveOperationsIndicator", () => {
	it("renders nothing while no operation runs or failed", async () => {
		downloads({ phase: "Completed" });
		renderWithProviders(
			<>
				<span data-testid="mounted" />
				<ActiveOperationsIndicator />
			</>,
			{ route: "/chat" },
		);

		await screen.findByTestId("mounted");
		expect(screen.queryByTestId("active-operations-indicator")).toBeNull();
	});

	it("counts running operations and links to Home", async () => {
		downloads({}, { phase: "Queued" });
		renderWithProviders(<ActiveOperationsIndicator />, { route: "/chat" });

		const chip = await screen.findByTestId("active-operations-indicator");
		expect(chip.textContent).toBe("2 running");
		expect(chip.getAttribute("data-state")).toBe("running");
		expect(chip.getAttribute("href")).toBe("/");
	});

	it("shows a single operation's reported percentage", async () => {
		downloads({ pct: 40 });
		renderWithProviders(<ActiveOperationsIndicator />, { route: "/models" });

		expect((await screen.findByTestId("active-operations-indicator")).textContent).toBe("1 running · 40%");
	});

	it("turns to the error state while any operation is failed, the runtime acquisition included", async () => {
		downloads({});
		sources.runtime = {
			sequence: 3,
			phase: "Failed",
			variant: null,
			tag: null,
			completedBytes: null,
			totalBytes: null,
			stepIndex: 1,
			stepCount: 1,
			sanitizedError: "Offline.",
		};
		renderWithProviders(<ActiveOperationsIndicator />, { route: "/settings" });

		const chip = await screen.findByTestId("active-operations-indicator");
		expect(chip.textContent).toBe("Action required");
		expect(chip.getAttribute("data-state")).toBe("failed");
		expect(chip.getAttribute("href")).toBe("/");
	});

	it("hides on Home, where the Activity card shows the same operations", async () => {
		downloads({ phase: "Failed" });
		renderWithProviders(
			<>
				<span data-testid="mounted" />
				<ActiveOperationsIndicator />
			</>,
			{ route: "/" },
		);

		await screen.findByTestId("mounted");
		expect(screen.queryByTestId("active-operations-indicator")).toBeNull();
	});
});
