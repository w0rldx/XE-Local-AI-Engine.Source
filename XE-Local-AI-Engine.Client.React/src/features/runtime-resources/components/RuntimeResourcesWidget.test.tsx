// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { capabilityState, toastMock } = vi.hoisted(() => ({
	capabilityState: { modelFit: true, images: true, transcription: true },
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
}));
vi.mock("@/capabilities/NodeCapabilities", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/capabilities/NodeCapabilities")>();
	return {
		...actual,
		nodeCapabilities: {
			...actual.nodeCapabilities,
			get modelFit() {
				return capabilityState.modelFit;
			},
			get images() {
				return capabilityState.images;
			},
			get transcription() {
				return capabilityState.transcription;
			},
		},
	};
});
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

// The hub's own lifecycle is pinned in useRuntimeResidencyHub.test; here it is a switch the test flips, and a record of
// the `enabled` the widget handed it.
const hubState = vi.hoisted(() => ({ isLive: false, enabledCalls: [] as boolean[] }));
vi.mock("@/core/api/signalr/useRuntimeResidencyHub", () => ({
	useRuntimeResidencyHub: (enabled: boolean) => {
		hubState.enabledCalls.push(enabled);
		return { isLive: hubState.isLive };
	},
}));

import type { QueryClient } from "@tanstack/react-query";

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { RuntimeResourcesWidget } from "@/features/runtime-resources/components/RuntimeResourcesWidget";
import { domainErrorRoute, jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// All three queries run against MSW through the real generated SDK and response validation. A test that expects the
// widget to stay dark declares no route, so any request it made would fail the test as undeclared.
const server = setupMswServer();

const gib = 1024 ** 3;

function gpu(index: number, totalGib: number, usedGib: number) {
	return {
		index,
		totalVramBytes: totalGib * gib,
		usedVramBytes: usedGib * gib,
		availableVramBytes: (totalGib - usedGib) * gib,
	};
}

function resident(overrides: Record<string, unknown> = {}) {
	return {
		modelName: "qwen3-4b",
		role: "chat",
		isResponsive: true,
		detail: "",
		detailCode: "responsive",
		isBusy: false,
		lastUsedUtc: null,
		isTransient: false,
		...overrides,
	};
}

function runtimeResident(overrides: Record<string, unknown> = {}) {
	return { runtime: "image", modelId: "sdxl-turbo", state: "idle", backend: null, canEject: true, ...overrides };
}

function serve(gpus: unknown[], residents: unknown[] = [], runtimeResidents: unknown[] = []): void {
	server.use(
		jsonRoute("get", "model-fit/resources", { totalRamBytes: 16 * gib, availableRamBytes: 4 * gib, gpus }),
		jsonRoute("get", "model-fit/running", { items: residents }),
		jsonRoute("get", "model-fit/runtime-residents", { items: runtimeResidents }),
	);
}

const idleActivity = {
	spawnReadinessCount: 0,
	residentProcessCount: 0,
	mutationReserved: false,
	evictionReserved: false,
	isBusy: false,
};

const imageRuntimeStatus = { managedRuntime: null, activity: { ...idleActivity, activeJobCount: 0 } };

const transcriptionRuntimeStatus = {
	enabled: true,
	state: "stopped",
	backend: null,
	binarySource: null,
	binaryVersion: null,
	loadedModelId: null,
	selectedModelId: null,
	recommendedModelId: "base",
	effectiveModelId: "base",
	supportsTranscode: true,
	idleTimeoutMinutes: 10,
	vadInstalled: false,
	processCaptureSupported: false,
	managedRuntime: null,
	activity: { ...idleActivity, activeTranscriptionCount: 0 },
};

async function openPopover(): Promise<HTMLElement> {
	renderWithProviders(<RuntimeResourcesWidget />, { withRouter: true });
	fireEvent.click(await screen.findByRole("button", { name: "Memory usage and loaded models" }));
	return screen.findByTestId("runtime-resources-dropdown");
}

// The refetchInterval the widget's observer of one generated operation asked for.
function refetchIntervalOf(queryClient: QueryClient, operationId: string): unknown {
	return queryClient.getQueryCache().findAll({ queryKey: [{ _id: operationId }] })[0]?.observers[0]?.options.refetchInterval;
}

beforeEach(() => {
	hubState.isLive = false;
	hubState.enabledCalls.length = 0;
	capabilityState.modelFit = true;
	capabilityState.images = true;
	capabilityState.transcription = true;
	useNodeAuthStore.getState().actions.setToken({ accessToken: "token", expiresAtUtc: "2099-01-01T00:00:00Z" });
});

afterEach(() => {
	cleanup();
	vi.clearAllMocks();
	useNodeAuthStore.getState().actions.clear();
});

describe("RuntimeResourcesWidget", () => {
	it("shows whole-machine RAM and VRAM with used, total and available", async () => {
		serve([gpu(0, 32, 8)], [resident()]);

		const dropdown = await openPopover();

		expect(screen.getByTestId("runtime-resources-count").textContent).toBe("1");
		const ram = within(dropdown).getByRole("progressbar", { name: "RAM" });
		expect(ram.getAttribute("aria-valuenow")).toBe("75");
		expect(within(dropdown).getByText("12.0 GB used of 16.0 GB, 4.0 GB available")).toBeTruthy();
		const vram = within(dropdown).getByRole("progressbar", { name: "GPU 0 VRAM" });
		expect(vram.getAttribute("aria-valuenow")).toBe("25");
		expect(within(dropdown).getByText("8.0 GB used of 32.0 GB, 24.0 GB available")).toBeTruthy();
		expect(within(dropdown).getByText("Idle")).toBeTruthy();
	});

	it("says VRAM usage is unavailable and draws no VRAM bar when no GPU was sampled", async () => {
		serve([]);

		const dropdown = await openPopover();

		expect(within(dropdown).getByText("VRAM usage unavailable")).toBeTruthy();
		expect(screen.queryByText("VRAM")).toBeNull();
		expect(screen.getAllByRole("progressbar").map((bar) => bar.getAttribute("aria-label"))).toEqual(["RAM"]);
	});

	it("draws one VRAM bar per GPU", async () => {
		serve([gpu(0, 24, 12), gpu(1, 16, 4)]);

		const dropdown = await openPopover();

		expect(within(dropdown).getByRole("progressbar", { name: "GPU 0 VRAM" }).getAttribute("aria-valuenow")).toBe("50");
		expect(within(dropdown).getByRole("progressbar", { name: "GPU 1 VRAM" }).getAttribute("aria-valuenow")).toBe("25");
	});

	it("disables eject while a resident is busy", async () => {
		serve([gpu(0, 32, 8)], [resident({ isBusy: true })]);

		const dropdown = await openPopover();

		expect(within(dropdown).getByText("Busy")).toBeTruthy();
		expect(within(dropdown).getByRole("button", { name: "Eject qwen3-4b" })).toHaveProperty("disabled", true);
	});

	it("ejects an idle resident gracefully, never with force", async () => {
		const ejectBodies: unknown[] = [];
		serve([gpu(0, 32, 8)], [resident({ isTransient: true })]);
		server.use(
			http.post(localApiPath("model-fit/running/eject"), async ({ request }) => {
				ejectBodies.push(await request.json());
				return HttpResponse.json({ modelName: "qwen3-4b", role: "chat", outcome: "ejected" });
			}),
		);

		const dropdown = await openPopover();
		expect(within(dropdown).getByText("Unloads soon")).toBeTruthy();
		fireEvent.click(within(dropdown).getByRole("button", { name: "Eject qwen3-4b" }));

		await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Model ejected."));
		expect(ejectBodies).toEqual([{ modelName: "qwen3-4b", role: "chat", force: false }]);
	});

	it("warns and leaves the model running when the graceful eject times out on a busy model", async () => {
		serve([gpu(0, 32, 8)], [resident()]);
		server.use(
			jsonRoute("post", "model-fit/running/eject", { modelName: "qwen3-4b", role: "chat", outcome: "timed_out_still_busy" }),
		);

		const dropdown = await openPopover();
		fireEvent.click(within(dropdown).getByRole("button", { name: "Eject qwen3-4b" }));

		await waitFor(() =>
			expect(toastMock.warning).toHaveBeenCalledWith("'qwen3-4b' is still finishing a response, so it was left running."),
		);
	});

	it("lists image and transcription residents with their runtime, state and backend", async () => {
		serve(
			[gpu(0, 32, 8)],
			[resident()],
			[
				runtimeResident({ state: "active" }),
				runtimeResident({ runtime: "transcription", modelId: "large-v3-turbo", backend: "cuda" }),
			],
		);

		const dropdown = await openPopover();

		expect(within(dropdown).getByText("Loaded models")).toBeTruthy();
		expect(within(within(dropdown).getByTestId("runtime-resources-row-qwen3-4b")).getByText("llama.cpp")).toBeTruthy();
		const image = await within(dropdown).findByTestId("runtime-resources-row-image-sdxl-turbo");
		expect(within(image).getByText("sdxl-turbo")).toBeTruthy();
		expect(within(image).getByText("Images")).toBeTruthy();
		expect(within(image).getByText("Active")).toBeTruthy();
		const transcription = within(dropdown).getByTestId("runtime-resources-row-transcription-large-v3-turbo");
		expect(within(transcription).getByText("large-v3-turbo")).toBeTruthy();
		expect(within(transcription).getByText("Transcription")).toBeTruthy();
		expect(within(transcription).getByText("CUDA")).toBeTruthy();
		expect(within(transcription).getByText("Idle")).toBeTruthy();
		expect(screen.getByTestId("runtime-resources-count").textContent).toBe("3");
		expect(within(dropdown).queryByText("No models are loaded.")).toBeNull();
	});

	it("shows a starting placeholder for a resident without a model id and cannot eject it", async () => {
		serve([gpu(0, 32, 8)], [], [runtimeResident({ runtime: "transcription", modelId: null, state: "starting" })]);

		const dropdown = await openPopover();

		const row = await within(dropdown).findByTestId("runtime-resources-row-transcription-starting");
		expect(within(row).getByText("Starting…")).toBeTruthy();
		expect(within(row).getByText("Starting")).toBeTruthy();
		expect(within(row).getByRole("button", { name: "Eject transcription runtime" })).toHaveProperty("disabled", true);
		expect(screen.getByTestId("runtime-resources-count").textContent).toBe("1");
	});

	it("hides image rows when the images surface is off", async () => {
		capabilityState.images = false;
		serve([gpu(0, 32, 8)], [], [runtimeResident(), runtimeResident({ runtime: "transcription", modelId: "base" })]);

		const dropdown = await openPopover();

		await within(dropdown).findByTestId("runtime-resources-row-transcription-base");
		expect(within(dropdown).queryByTestId("runtime-resources-row-image-sdxl-turbo")).toBeNull();
		expect(screen.getByTestId("runtime-resources-count").textContent).toBe("1");
	});

	it("hides transcription rows when the transcription surface is off", async () => {
		capabilityState.transcription = false;
		serve([gpu(0, 32, 8)], [], [runtimeResident(), runtimeResident({ runtime: "transcription", modelId: "base" })]);

		const dropdown = await openPopover();

		await within(dropdown).findByTestId("runtime-resources-row-image-sdxl-turbo");
		expect(within(dropdown).queryByTestId("runtime-resources-row-transcription-base")).toBeNull();
	});

	it("never asks for image or transcription residents when both surfaces are off", async () => {
		capabilityState.images = false;
		capabilityState.transcription = false;
		// No residents route: a request for one would fail the test as undeclared.
		server.use(
			jsonRoute("get", "model-fit/resources", { totalRamBytes: 16 * gib, availableRamBytes: 4 * gib, gpus: [] }),
			jsonRoute("get", "model-fit/running", { items: [resident()] }),
		);

		const dropdown = await openPopover();

		expect(await within(dropdown).findByTestId("runtime-resources-row-qwen3-4b")).toBeTruthy();
		expect(screen.getByTestId("runtime-resources-count").textContent).toBe("1");
	});

	it("disables eject while the server says the runtime cannot be ejected", async () => {
		serve([gpu(0, 32, 8)], [], [runtimeResident({ canEject: false })]);

		const dropdown = await openPopover();

		expect(await within(dropdown).findByRole("button", { name: "Eject image runtime" })).toHaveProperty("disabled", true);
	});

	it("ejects each runtime through its own endpoint and refreshes the residents", async () => {
		const ejected: string[] = [];
		let residentReads = 0;
		server.use(
			jsonRoute("get", "model-fit/resources", { totalRamBytes: 16 * gib, availableRamBytes: 4 * gib, gpus: [] }),
			jsonRoute("get", "model-fit/running", { items: [] }),
			http.get(localApiPath("model-fit/runtime-residents"), () => {
				residentReads += 1;
				return HttpResponse.json({
					items: [runtimeResident(), runtimeResident({ runtime: "transcription", modelId: "base", backend: "cpu" })],
				});
			}),
			http.post(localApiPath("images/runtime/eject"), () => {
				ejected.push("image");
				return HttpResponse.json(imageRuntimeStatus);
			}),
			http.post(localApiPath("transcription/runtime/eject"), () => {
				ejected.push("transcription");
				return HttpResponse.json(transcriptionRuntimeStatus);
			}),
		);

		const dropdown = await openPopover();
		fireEvent.click(await within(dropdown).findByRole("button", { name: "Eject image runtime" }));
		await waitFor(() => expect(residentReads).toBe(2));
		fireEvent.click(within(dropdown).getByRole("button", { name: "Eject transcription runtime" }));

		await waitFor(() => expect(residentReads).toBe(3));
		expect(ejected).toEqual(["image", "transcription"]);
		expect(toastMock.error).not.toHaveBeenCalled();
	});

	it("shows the server's reason when the image eject is refused", async () => {
		serve([gpu(0, 32, 8)], [], [runtimeResident()]);
		server.use(
			domainErrorRoute("post", "images/runtime/eject", 409, {
				reason: "runtime-busy",
				message: "An image job is still running.",
				activity: { ...idleActivity, activeJobCount: 1 },
			}),
		);

		const dropdown = await openPopover();
		fireEvent.click(await within(dropdown).findByRole("button", { name: "Eject image runtime" }));

		await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("An image job is still running."));
	});

	it("keeps the gauge at 5s and holds the resident lists to the push floor while the hub is live", async () => {
		hubState.isLive = true;
		serve([gpu(0, 32, 8)], [resident()], [runtimeResident()]);

		const { queryClient } = renderWithProviders(<RuntimeResourcesWidget />, { withRouter: true });
		await screen.findByTestId("runtime-resources-trigger");

		expect(hubState.enabledCalls).toContain(true);
		expect(refetchIntervalOf(queryClient, "getRuntimeResources")).toBe(5000);
		expect(refetchIntervalOf(queryClient, "listRunningModels")).toBe(60_000);
		expect(refetchIntervalOf(queryClient, "getRuntimeResidents")).toBe(60_000);
	});

	it("polls the resident lists at their fallback cadence while the hub is degraded", async () => {
		serve([gpu(0, 32, 8)], [resident()], [runtimeResident()]);

		const { queryClient } = renderWithProviders(<RuntimeResourcesWidget />, { withRouter: true });
		await screen.findByTestId("runtime-resources-trigger");

		expect(refetchIntervalOf(queryClient, "getRuntimeResources")).toBe(5000);
		expect(refetchIntervalOf(queryClient, "listRunningModels")).toBe(4000);
		expect(refetchIntervalOf(queryClient, "getRuntimeResidents")).toBe(5000);
	});

	it("renders nothing and polls nothing below the desktop breakpoint", async () => {
		Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: 390 });

		try {
			const { container } = renderWithProviders(<RuntimeResourcesWidget />, { withRouter: true });

			// No route is declared, so a poll from a narrow window would fail the test as an undeclared request.
			await waitFor(() => expect(container.querySelector("[data-testid='runtime-resources-trigger']")).toBeNull());
			expect(screen.queryByRole("button", { name: "Memory usage and loaded models" })).toBeNull();
			expect(hubState.enabledCalls.length).toBeGreaterThan(0);
			expect(hubState.enabledCalls).not.toContain(true);
		} finally {
			Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: 1024 });
		}
	});

	it("renders nothing and polls nothing when the model-fit surface is off", async () => {
		capabilityState.modelFit = false;

		const { container } = renderWithProviders(<RuntimeResourcesWidget />, { withRouter: true });

		// The router mounts asynchronously; wait for it before asserting the widget stayed dark.
		await waitFor(() => expect(container.querySelector("[data-testid='runtime-resources-trigger']")).toBeNull());
		expect(screen.queryByRole("button", { name: "Memory usage and loaded models" })).toBeNull();
		expect(hubState.enabledCalls.length).toBeGreaterThan(0);
		expect(hubState.enabledCalls).not.toContain(true);
	});

	it("renders nothing and polls nothing without a signed-in session", async () => {
		useNodeAuthStore.getState().actions.clear();

		renderWithProviders(<RuntimeResourcesWidget />, { withRouter: true });

		await waitFor(() => expect(screen.queryByTestId("runtime-resources-trigger")).toBeNull());
		// Signed out the hub would fail to negotiate, so it must not even try.
		expect(hubState.enabledCalls.length).toBeGreaterThan(0);
		expect(hubState.enabledCalls).not.toContain(true);
	});
});
