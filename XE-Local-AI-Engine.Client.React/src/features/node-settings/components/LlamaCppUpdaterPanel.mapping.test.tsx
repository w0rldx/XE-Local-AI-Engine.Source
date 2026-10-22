// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

import { toast } from "@/core/ui/notifications/Toast";
import { LlamaCppUpdaterPanel } from "@/features/node-settings/components/LlamaCppUpdaterPanel";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
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
	installed: { tag: "b9692", variant: "cpu", asset: "a", installedAtUtc: 0, isSourceBuild: false, operatorSelected: false },
	recommendedTag: "b9692",
	upstreamLatestTag: null,
	updateAvailable: false,
	isOffline: false,
	runningProcessCount: 0,
	isSourceBuild: false,
	rebuildAvailable: false,
	checkedAtUtc: 1700000000000,
	selectedVariant: "cpu",
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
	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

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
		expect(screen.queryByTestId("llamacpp-updater-override-notice")).toBeNull();
	});

	it("says a bring-your-own override serves instead of the installed runtime", async () => {
		server.use(
			jsonRoute("get", "model-fit/llamacpp/runtime", { ...runtimeStatusDto, overrideVariant: "cuda" }),
			jsonRoute("get", "model-fit/llamacpp/source-build/status", sourceBuildStatusDto),
		);
		renderWithProviders(<LlamaCppUpdaterPanel />);

		// The installed CPU build stays listed; the notice is what stops the card implying that build serves.
		expect((await screen.findByTestId("llamacpp-updater-override-notice")).textContent).toBe(
			"A bring-your-own CUDA llama-server is serving models on this node. The installed runtime below is not in use while the override is set.",
		);
	});

	it("reads the pin and the selector's answer off the wire", async () => {
		server.use(
			jsonRoute("get", "model-fit/llamacpp/runtime", {
				...runtimeStatusDto,
				installed: { ...runtimeStatusDto.installed, operatorSelected: true },
				selectedVariant: "cpu",
			}),
			jsonRoute("get", "model-fit/llamacpp/source-build/status", sourceBuildStatusDto),
		);
		renderWithProviders(<LlamaCppUpdaterPanel />);

		expect((await screen.findByTestId("llamacpp-updater-selection")).textContent).toBe("Pinned by you: CPU");
	});

	it("names the pin the process holds when the record names another variant", async () => {
		server.use(
			jsonRoute("get", "model-fit/llamacpp/runtime", {
				...runtimeStatusDto,
				installed: { ...runtimeStatusDto.installed, variant: "cuda", operatorSelected: true },
				selectedVariant: "vulkan",
			}),
			jsonRoute("get", "model-fit/llamacpp/source-build/status", sourceBuildStatusDto),
		);
		renderWithProviders(<LlamaCppUpdaterPanel />);

		expect((await screen.findByTestId("llamacpp-updater-selection")).textContent).toBe("Pinned by you: Vulkan");
		expect(screen.getByTestId("llamacpp-updater-use-automatic-button")).toBeTruthy();
	});

	it("restores automatic selection and refetches the runtime", async () => {
		let pinned = true;
		server.use(
			http.get(localApiPath("model-fit/llamacpp/runtime"), () =>
				HttpResponse.json({
					...runtimeStatusDto,
					installed: { ...runtimeStatusDto.installed, operatorSelected: pinned },
					selectedVariant: "vulkan",
				}),
			),
			http.post(localApiPath("model-fit/llamacpp/variant/automatic"), () => {
				pinned = false;
				return HttpResponse.json({ ...runtimeStatusDto, selectedVariant: "vulkan" });
			}),
			jsonRoute("get", "model-fit/llamacpp/source-build/status", sourceBuildStatusDto),
		);
		renderWithProviders(<LlamaCppUpdaterPanel />);

		fireEvent.click(await screen.findByTestId("llamacpp-updater-use-automatic-button"));

		await waitFor(() => expect(screen.getByTestId("llamacpp-updater-selection").textContent).toBe("Automatic: Vulkan"));
		expect(toast.success).toHaveBeenCalledWith("Automatic variant selection restored.");
	});

	it("shows the 409 message while a bring-your-own override blocks automatic selection", async () => {
		server.use(
			jsonRoute("get", "model-fit/llamacpp/runtime", {
				...runtimeStatusDto,
				installed: { ...runtimeStatusDto.installed, operatorSelected: true },
				selectedVariant: "cpu",
			}),
			http.post(localApiPath("model-fit/llamacpp/variant/automatic"), () =>
				HttpResponse.json(
					{ runningProcessCount: 0, message: "Clear the bring-your-own llama-server override first." },
					{ status: 409 },
				),
			),
			jsonRoute("get", "model-fit/llamacpp/source-build/status", sourceBuildStatusDto),
		);
		renderWithProviders(<LlamaCppUpdaterPanel />);

		fireEvent.click(await screen.findByTestId("llamacpp-updater-use-automatic-button"));

		await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Clear the bring-your-own llama-server override first."));
	});
});
