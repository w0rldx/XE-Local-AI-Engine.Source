// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import en from "@/locales/en.json";
import { domainErrorRoute, jsonRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// The Managed Python card: the shared toolchain, one row per feature environment in the server's own state and
// reason, Compute's confirmed repair/remove with the node's typed 409 refusal surfaced verbatim, and a status poll
// that runs only while something is provisioning.

const { toastMock } = vi.hoisted(() => ({
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { ManagedPythonCard } from "@/features/node-settings/components/ManagedPythonCard";

const copy = en.pages.nodeSettings.managedPython;

interface EnvironmentOverrides {
	readonly state?: string;
	readonly reason?: string | null;
	readonly installed?: boolean;
	readonly mismatches?: readonly string[];
}

function environment(profileId: string, overrides: EnvironmentOverrides = {}) {
	return {
		profileId,
		state: overrides.state ?? "Ready",
		reason: overrides.reason ?? null,
		installed:
			overrides.installed === false
				? null
				: { pythonMinor: "3.13", profileRevision: 1, rid: "linux-x64", probeContractVersion: 1, uvVersion: "0.12.19" },
		mismatches: overrides.mismatches ?? [],
	};
}

function status(
	environments: readonly ReturnType<typeof environment>[],
	toolchain: { uvPresent?: boolean; pythonInstalls?: readonly string[] } = {},
) {
	return {
		toolchain: {
			uvVersion: "0.12.19",
			uvPresent: toolchain.uvPresent ?? true,
			pythonInstalls: toolchain.pythonInstalls ?? ["cpython-3.13.15-linux-x86_64-gnu"],
		},
		environments,
	};
}

const statusPath = "python/status";

/** Counts status reads, so a test can assert the refetch after a mutation and the poll cadence. */
function countStatusReads(): { readonly count: () => number } {
	let reads = 0;
	server.events.on("request:start", ({ request }) => {
		if (request.method === "GET" && request.url.endsWith(`/${statusPath}`)) {
			reads += 1;
		}
	});
	return { count: () => reads };
}

setupMswServer();

describe("ManagedPythonCard", () => {
	beforeEach(() => {
		toastMock.error.mockClear();
	});

	afterEach(() => {
		cleanup();
		server.events.removeAllListeners();
		vi.useRealTimers();
	});

	it("renders the toolchain and every environment state as text", async () => {
		const states = Object.keys(copy.states) as (keyof typeof copy.states)[];
		server.use(
			jsonRoute(
				"get",
				statusPath,
				status(states.map((state) => environment(state === "Ready" ? "training" : `profile-${state}`, { state }))),
			),
		);
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		expect(await screen.findByText(copy.uvPresent)).toBeTruthy();
		expect(screen.getByText("cpython-3.13.15-linux-x86_64-gnu")).toBeTruthy();
		for (const state of states) {
			expect(screen.getByText(copy.states[state])).toBeTruthy();
		}
	});

	it("says plainly when no interpreter is installed and uv is missing", async () => {
		server.use(jsonRoute("get", statusPath, status([], { uvPresent: false, pythonInstalls: [] })));
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		expect(await screen.findByText(copy.noPythonInstalls)).toBeTruthy();
		expect(screen.getByText(copy.uvMissing)).toBeTruthy();
	});

	// 5.5: on a platform no environment supports, uv is never downloaded; "not downloaded yet" promised otherwise.
	it("says uv is not available on this platform when every environment is unsupported", async () => {
		server.use(
			jsonRoute(
				"get",
				statusPath,
				status(
					[
						environment("training", { state: "Unsupported", installed: false }),
						environment("compute", { state: "Unsupported", installed: false }),
					],
					{ uvPresent: false, pythonInstalls: [] },
				),
			),
		);
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		expect((await screen.findByTestId("managed-python-uv-badge")).textContent).toBe(copy.uvUnsupported);
		expect(screen.queryByText(copy.uvMissing)).toBeNull();
	});

	it("keeps saying uv is not downloaded yet while one environment is supported", async () => {
		server.use(
			jsonRoute(
				"get",
				statusPath,
				status(
					[
						environment("training", { state: "Unsupported", installed: false }),
						environment("compute", { state: "NotProvisioned", installed: false }),
					],
					{ uvPresent: false, pythonInstalls: [] },
				),
			),
		);
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		expect((await screen.findByTestId("managed-python-uv-badge")).textContent).toBe(copy.uvMissing);
	});

	it("links the Training row to the Training page instead of offering actions", async () => {
		server.use(jsonRoute("get", statusPath, status([environment("training")])));
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		const link = await screen.findByRole("link", { name: copy.trainingLink });
		expect(link.getAttribute("href")).toBe("/training");
		const row = screen.getByTestId("managed-python-environment-training");
		expect(within(row).getByText(copy.profiles.training)).toBeTruthy();
		expect(within(row).queryByRole("button")).toBeNull();
	});

	it("lists the localized mismatches of an environment that needs an update", async () => {
		server.use(
			jsonRoute(
				"get",
				statusPath,
				status([environment("compute", { state: "UpdateRequired", mismatches: ["lockfile", "pythonMinor"] })]),
			),
		);
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		expect(await screen.findByText(copy.mismatches.lockfile)).toBeTruthy();
		expect(screen.getByText(copy.mismatches.pythonMinor)).toBeTruthy();
		expect(screen.getByText(copy.mismatchesTitle)).toBeTruthy();
	});

	it("shows the reason and disables both Compute actions while Compute is unsupported", async () => {
		const reason = "Compute is disabled on this node (Compute:Enabled=false).";
		server.use(
			jsonRoute("get", statusPath, status([environment("compute", { state: "Unsupported", reason, installed: false })])),
		);
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		expect(await screen.findByText(reason)).toBeTruthy();
		expect((screen.getByRole("button", { name: copy.repair }) as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByRole("button", { name: copy.remove }) as HTMLButtonElement).disabled).toBe(true);
	});

	it("removes Compute only after the confirmation and shows the status the node returned", async () => {
		const removed = vi.fn();
		server.use(
			jsonRoute("get", statusPath, status([environment("compute")])),
			jsonRoute("post", "python/environments/compute/remove", status([environment("compute", { state: "NotProvisioned" })])),
		);
		server.events.on("request:start", ({ request }) => {
			if (request.method === "POST" && request.url.endsWith("/compute/remove")) {
				removed();
			}
		});
		const reads = countStatusReads();
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		const remove = await screen.findByRole("button", { name: copy.remove });
		await waitFor(() => expect((remove as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(remove);
		const dialog = await screen.findByRole("dialog");
		expect(within(dialog).getByText(copy.removeConfirmBody)).toBeTruthy();
		expect(removed).not.toHaveBeenCalled();

		const readsBefore = reads.count();
		fireEvent.click(within(dialog).getByRole("button", { name: copy.removeConfirm }));
		await waitFor(() => expect(removed).toHaveBeenCalledTimes(1));
		// The POST body IS the fresh status: the card shows it without another GET (the stubbed GET still says Ready).
		expect(await screen.findByText(copy.states.NotProvisioned)).toBeTruthy();
		expect(screen.queryByText(copy.states.Ready)).toBeNull();
		expect(reads.count()).toBe(readsBefore);
		expect(toastMock.error).not.toHaveBeenCalled();
	});

	it("falls back to the raw name for a state, profile or mismatch it does not know, even an inherited one", async () => {
		server.use(
			jsonRoute(
				"get",
				statusPath,
				status([
					environment("constructor", { state: "toString" }),
					environment("compute", { state: "UpdateRequired", mismatches: ["valueOf"] }),
				]),
			),
		);
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		expect(await screen.findByText("toString")).toBeTruthy();
		expect(screen.getByText("constructor")).toBeTruthy();
		expect(screen.getByText("valueOf")).toBeTruthy();
	});

	it("announces the loading state politely", async () => {
		server.use(jsonRoute("get", statusPath, status([])));
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		const loading = await screen.findByRole("status");
		expect(loading.getAttribute("aria-live")).toBe("polite");
		expect(within(loading).getByText(copy.loading)).toBeTruthy();
		expect(await screen.findByText("cpython-3.13.15-linux-x86_64-gnu")).toBeTruthy();
		expect(screen.queryByRole("status")).toBeNull();
	});

	it("surfaces the node's own words when a repair is refused as busy", async () => {
		const message = "A Python tool call is running in the Compute environment; try again when it finishes.";
		server.use(
			jsonRoute("get", statusPath, status([environment("compute")])),
			domainErrorRoute("post", "python/environments/compute/repair", 409, { reason: "busy", message }),
		);
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		const repair = await screen.findByRole("button", { name: copy.repair });
		await waitFor(() => expect((repair as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(repair);
		fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: copy.repairConfirm }));

		await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(message));
	});

	it("polls while an environment is provisioning and disables the Compute actions meanwhile", async () => {
		vi.useFakeTimers({ shouldAdvanceTime: true });
		server.use(jsonRoute("get", statusPath, status([environment("compute", { state: "Provisioning", installed: false })])));
		const reads = countStatusReads();
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		expect(await screen.findByText(copy.states.Provisioning)).toBeTruthy();
		expect((screen.getByRole("button", { name: copy.repair }) as HTMLButtonElement).disabled).toBe(true);
		const first = reads.count();
		await vi.advanceTimersByTimeAsync(5_100);
		await waitFor(() => expect(reads.count()).toBeGreaterThan(first));
	});

	it("does not poll once every environment has settled", async () => {
		vi.useFakeTimers({ shouldAdvanceTime: true });
		server.use(jsonRoute("get", statusPath, status([environment("compute")])));
		const reads = countStatusReads();
		renderWithProviders(<ManagedPythonCard />, { withRouter: true });

		expect(await screen.findByText(copy.states.Ready)).toBeTruthy();
		const first = reads.count();
		await vi.advanceTimersByTimeAsync(15_000);
		expect(reads.count()).toBe(first);
	});
});
