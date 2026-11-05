// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { HomeActivityCard } from "@/features/home/components/HomeActivityCard";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The source feeds are hub-backed hooks with their own tests; the merge, normalization and retry mutation between
// them and the card run for real, down to the generated ensure call.
const { sources, ensure } = vi.hoisted(() => ({
	sources: { gguf: new Map<string, unknown>(), runtime: undefined as unknown },
	ensure: vi.fn(),
}));

vi.mock("@/core/model-downloads/queries/useGgufAcquisitions", () => ({
	useActiveGgufAcquisitions: () => sources.gguf,
}));
vi.mock("@/core/runtime-acquisition/hooks/useRuntimeAcquisitionHub", () => ({
	useRuntimeAcquisitionHub: () => sources.runtime,
}));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	ensureLlamaCppBinaryMutation: () => ({ mutationFn: (variables: unknown) => ensure(variables) }),
}));

function download(overrides: Record<string, unknown>) {
	sources.gguf = new Map([
		[
			"op-1",
			{
				operationId: "op-1",
				operationKind: "Download",
				modelName: "qwen3-8b",
				phase: "Downloading",
				pct: undefined,
				completedBytes: null,
				totalBytes: null,
				startedAtUtc: null,
				updatedAtUtc: null,
				errorCode: null,
				sanitizedMessage: null,
				...overrides,
			},
		],
	]);
}

function failedRuntime() {
	sources.runtime = {
		sequence: 5,
		phase: "Failed",
		variant: "Vulkan",
		tag: "b9692",
		completedBytes: null,
		totalBytes: null,
		stepIndex: 1,
		stepCount: 1,
		sanitizedError: "The download was interrupted.",
	};
}

beforeEach(() => {
	sources.gguf = new Map();
	sources.runtime = undefined;
	ensure.mockReset();
	ensure.mockResolvedValue({});
});

afterEach(cleanup);

describe("HomeActivityCard", () => {
	it("is not rendered while nothing runs or failed", async () => {
		download({ phase: "Completed" });
		renderWithProviders(
			<>
				<span data-testid="mounted" />
				<HomeActivityCard />
			</>,
			{ route: "/" },
		);

		await screen.findByTestId("mounted");
		expect(screen.queryByTestId("home-activity")).toBeNull();
	});

	it("lists a running download with its reported progress and a link to the models page", async () => {
		download({ pct: 50, completedBytes: 2048, totalBytes: 4096 });
		renderWithProviders(<HomeActivityCard />, { route: "/" });

		const row = await screen.findByTestId("home-activity-item");
		expect(row.getAttribute("data-state")).toBe("running");
		expect(row.textContent).toContain("Downloading qwen3-8b");
		expect(screen.getByTestId("home-activity-detail").textContent).toBe("2 KB of 4 KB");
		expect(screen.getByRole("progressbar", { name: "Downloading qwen3-8b" }).getAttribute("aria-valuenow")).toBe("50");
		expect(screen.getByTestId("home-activity-open").getAttribute("href")).toBe("/models");
		expect(screen.queryByTestId("home-activity-retry")).toBeNull();
	});

	it("shows no bar for indeterminate progress", async () => {
		download({ phase: "Queued" });
		renderWithProviders(<HomeActivityCard />, { route: "/" });

		await screen.findByTestId("home-activity-item");
		expect(screen.queryByRole("progressbar")).toBeNull();
	});

	it("keeps a runtime failure with its reason, a link to the runtimes section and a working retry", async () => {
		failedRuntime();
		renderWithProviders(<HomeActivityCard />, { route: "/" });

		const row = await screen.findByTestId("home-activity-item");
		expect(row.getAttribute("data-state")).toBe("failed");
		expect(row.textContent).toContain("The local AI runtime could not be installed");
		expect(screen.getByTestId("home-activity-detail").textContent).toBe("The download was interrupted.");
		expect(screen.getByTestId("home-activity-open").getAttribute("href")).toBe("/node-settings?section=runtimes");

		fireEvent.click(screen.getByTestId("home-activity-retry"));

		await waitFor(() => expect(ensure).toHaveBeenCalledWith({ body: { variant: "vulkan" } }));
	});
});
