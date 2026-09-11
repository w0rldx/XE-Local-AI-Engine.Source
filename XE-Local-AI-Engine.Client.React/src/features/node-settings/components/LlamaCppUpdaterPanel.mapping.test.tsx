// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { LlamaCppUpdaterPanel } from "@/features/node-settings/components/LlamaCppUpdaterPanel";
import { jsonRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// A SIBLING file, because LlamaCppUpdaterPanel.test.tsx `vi.mock`s the whole useLocalRuntime module: under that harness
// `toLlamaCppRuntimeStatus` never runs, so a test there would stay green with the mapper line deleted. Here the DTO is
// served over the wire and the query, its `select` mapper and the panel all run for real.
setupMswServer();

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { progress: vi.fn(), success: vi.fn(), error: vi.fn() },
}));

const runtimeStatusDto = {
	installed: { tag: "b9692", variant: "cpu", asset: "a", installedAtUtc: 0, isSourceBuild: false },
	recommendedTag: "b9692",
	upstreamLatestTag: null,
	updateAvailable: false,
	isOffline: false,
	runningProcessCount: 0,
	isSourceBuild: false,
	rebuildAvailable: false,
	checkedAtUtc: 1700000000000,
};

const sourceBuildStatusDto = {
	phase: "Idle",
	isRunning: false,
	terminal: false,
	logStartSequence: 0,
	logLines: [],
	currentBuild: null,
};

describe("LlamaCppUpdaterPanel over the real query mapping", () => {
	afterEach(() => cleanup());

	it("resolves the checked-at timestamp from the wire through the query mapping", async () => {
		server.use(
			jsonRoute("get", "model-fit/llamacpp/runtime", runtimeStatusDto),
			jsonRoute("get", "model-fit/llamacpp/source-build/status", sourceBuildStatusDto),
		);
		renderWithProviders(<LlamaCppUpdaterPanel />);

		// Delete `checkedAtUtc` from LocalRuntimeMappers and this flips to the not-checked badge; that is the whole point
		// of the file.
		expect(await screen.findByTestId("llamacpp-updater-state-uptodate")).toBeTruthy();
		expect(screen.queryByTestId("llamacpp-updater-state-notchecked")).toBeNull();
	});
});
