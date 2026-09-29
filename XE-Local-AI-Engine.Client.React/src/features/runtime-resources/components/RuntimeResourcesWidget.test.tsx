// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { capabilityState, toastMock } = vi.hoisted(() => ({
	capabilityState: { modelFit: true },
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
		},
	};
});
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { RuntimeResourcesWidget } from "@/features/runtime-resources/components/RuntimeResourcesWidget";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// Both polls run against MSW through the real generated SDK and response validation. A test that expects the widget
// to stay dark declares no route, so any request it made would fail the test as undeclared.
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

function serve(gpus: unknown[], residents: unknown[] = []): void {
	server.use(
		jsonRoute("get", "model-fit/resources", { totalRamBytes: 16 * gib, availableRamBytes: 4 * gib, gpus }),
		jsonRoute("get", "model-fit/running", { items: residents }),
	);
}

async function openPopover(): Promise<HTMLElement> {
	renderWithProviders(<RuntimeResourcesWidget />, { withRouter: true });
	fireEvent.click(await screen.findByRole("button", { name: "Memory usage and loaded models" }));
	return screen.findByTestId("runtime-resources-dropdown");
}

beforeEach(() => {
	capabilityState.modelFit = true;
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

	it("renders nothing and polls nothing below the desktop breakpoint", async () => {
		Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: 390 });

		try {
			const { container } = renderWithProviders(<RuntimeResourcesWidget />, { withRouter: true });

			// No route is declared, so a poll from a narrow window would fail the test as an undeclared request.
			await waitFor(() => expect(container.querySelector("[data-testid='runtime-resources-trigger']")).toBeNull());
			expect(screen.queryByRole("button", { name: "Memory usage and loaded models" })).toBeNull();
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
	});

	it("renders nothing and polls nothing without a signed-in session", async () => {
		useNodeAuthStore.getState().actions.clear();

		renderWithProviders(<RuntimeResourcesWidget />, { withRouter: true });

		await waitFor(() => expect(screen.queryByTestId("runtime-resources-trigger")).toBeNull());
	});
});
