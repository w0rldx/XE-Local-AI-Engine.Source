// @vitest-environment jsdom

// A row opened while it ran, with no hub at all: the view reads its saved row until it ends.

import { screen } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { TraceExperimentView } from "@/features/modelLab/components/TraceExperimentView";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const pollMs = vi.hoisted(() => vi.fn((isLive: boolean) => (isLive ? 60_000 : 20)));

vi.mock("@/features/modelLab/models/ModelLabTrace", async (original) => ({
	...(await original<typeof import("@/features/modelLab/models/ModelLabTrace")>()),
	activeJobPollMs: pollMs,
}));

setupMswServer();

const experimentId = "11111111-1111-4111-8111-111111111111";

describe("TraceExperimentView", () => {
	it("reaches a running row's failure with no hub event", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(`model-lab/experiments/${experimentId}`), () => {
				reads += 1;
				const failed = reads > 1;
				return HttpResponse.json({
					id: experimentId,
					status: failed ? "Failed" : "Running",
					repoId: "Qwen/Qwen3.5-0.8B",
					revision: "rev",
					installedModelName: "org/Tiny-GGUF:Q4_K_M",
					prompt: "hello",
					errorMessage: failed ? "The script failed." : null,
					traceByteCount: null,
					result: null,
					createdAtUtc: "2026-11-03T00:00:00Z",
					updatedAtUtc: "2026-11-03T00:00:00Z",
				});
			}),
		);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		expect(await screen.findByText("The script failed.")).toBeTruthy();
	});

	it.each(["Failed", "Cancelled", "Running"])("does not call a %s row with no trace measured", async (status) => {
		server.use(
			http.get(localApiPath(`model-lab/experiments/${experimentId}`), () =>
				HttpResponse.json({
					id: experimentId,
					status,
					repoId: "Qwen/Qwen3.5-0.8B",
					revision: "rev",
					installedModelName: "org/Tiny-GGUF:Q4_K_M",
					prompt: "hello",
					errorMessage: null,
					traceByteCount: null,
					result: null,
					createdAtUtc: "2026-11-03T00:00:00Z",
					updatedAtUtc: "2026-11-03T00:00:00Z",
				}),
			),
		);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={true} />);

		expect((await screen.findByTestId("model-lab-trace-view")).textContent).not.toContain("Read by hooks");
		expect(screen.queryByTestId("model-lab-trace-measured")).toBeNull();
	});

	it("keeps only the floor while the hub is live", async () => {
		server.use(
			http.get(localApiPath(`model-lab/experiments/${experimentId}`), () =>
				HttpResponse.json({
					id: experimentId,
					status: "Running",
					repoId: "Qwen/Qwen3.5-0.8B",
					revision: "rev",
					installedModelName: "org/Tiny-GGUF:Q4_K_M",
					prompt: "hello",
					errorMessage: null,
					traceByteCount: null,
					result: null,
					createdAtUtc: "2026-11-03T00:00:00Z",
					updatedAtUtc: "2026-11-03T00:00:00Z",
				}),
			),
		);
		pollMs.mockClear();

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={true} />);

		await screen.findByTestId("model-lab-trace-view");
		expect(pollMs).toHaveBeenCalledWith(true);
		expect(pollMs).not.toHaveBeenCalledWith(false);
	});
});
