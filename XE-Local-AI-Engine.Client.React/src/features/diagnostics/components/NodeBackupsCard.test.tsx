// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import "@/i18n";

const fixtures = vi.hoisted(() => ({
	confirm: vi.fn((_options: unknown) => Promise.resolve(true)),
}));

vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: fixtures.confirm }),
}));

import { NodeBackupsCard } from "@/features/diagnostics/components/NodeBackupsCard";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";
import { jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { setupMswServer } from "@/test/UseMswServer";

const snapshotName = "node-chat-20261008T120000Z.sqlite";

const server = setupMswServer();

let listCalls = 0;

function listRoute(lastAutomaticBackup: Record<string, unknown> = { outcome: "NotRun" }) {
	return http.get(localApiPath("node/backups"), () => {
		listCalls += 1;
		return HttpResponse.json({
			backups: [{ name: snapshotName, sizeBytes: 2_097_152, createdUtc: "2026-10-08T12:00:00Z" }],
			lastAutomaticBackup,
		});
	});
}

// The card reads only isLocalMode; the rest satisfies the generated response schema.
function nodeInfoRoute(isLocalMode: boolean) {
	return jsonRoute("get", "diagnostics/node-info", {
		capturedAtUtc: "2026-10-08T12:00:00+00:00",
		version: "1.0.0",
		commit: null,
		flavour: "tester",
		selectedChannel: "preview",
		defaultChannel: "preview",
		repositoryUrl: null,
		isLocalMode,
		isShellOwned: false,
		osDescription: "Linux",
		osArchitecture: "X64",
		processArchitecture: "X64",
		runtimeFramework: ".NET 10.0.0",
		cpuModel: null,
		cpuCores: null,
		totalRamBytes: null,
		availableRamBytes: null,
		freeDiskBytes: null,
		gpuVendor: null,
		inferenceBackend: null,
		cpuFallback: false,
		gpus: null,
		liveGpuMemory: null,
		runtimes: null,
		residents: null,
		models: null,
		runningModels: null,
		settings: null,
		uptimeSeconds: 1,
		verboseLogging: false,
		warnings: [],
	});
}

function renderCard() {
	return render(
		<QueryClientProvider client={new QueryClient()}>
			<MantineProvider env="test" theme={testMantineTheme}>
				<NodeBackupsCard />
			</MantineProvider>
		</QueryClientProvider>,
	);
}

describe("NodeBackupsCard", () => {
	beforeEach(() => {
		listCalls = 0;
		fixtures.confirm.mockClear();
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("lists the snapshots and the failed automatic backup with its reason", async () => {
		server.use(nodeInfoRoute(true), listRoute({ outcome: "Failed", atUtc: "2026-10-08T11:59:00Z", error: "disk full" }));

		renderCard();

		expect(await screen.findByText(snapshotName)).toBeTruthy();
		expect(screen.getByText("2 MB")).toBeTruthy();
		expect(screen.getByText(/Automatic backup at this start: failed .*disk full/)).toBeTruthy();
	});

	it("takes a snapshot and refreshes the list", async () => {
		let posts = 0;
		server.use(
			nodeInfoRoute(true),
			listRoute(),
			http.post(localApiPath("node/backups"), () => {
				posts += 1;
				return HttpResponse.json({ name: snapshotName, sizeBytes: 1, createdUtc: "2026-10-08T12:00:00Z" }, { status: 201 });
			}),
		);

		renderCard();
		const take = await screen.findByRole("button", { name: "Take snapshot" });
		await waitFor(() => expect((take as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(take);

		await waitFor(() => expect(posts).toBe(1));
		await waitFor(() => expect(listCalls).toBe(2));
	});

	it("shows the node's reason when the free-space guard refuses a snapshot", async () => {
		server.use(
			nodeInfoRoute(true),
			listRoute(),
			problemDetailsRoute("post", "node/backups", 507, { title: "There is not enough free disk space for a database backup." }),
		);

		renderCard();
		const take = await screen.findByRole("button", { name: "Take snapshot" });
		await waitFor(() => expect((take as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(take);

		expect(await screen.findByText("There is not enough free disk space for a database backup.")).toBeTruthy();
	});

	it("restores after confirmation, then shows the stopping notice and disables the card", async () => {
		const restored: string[] = [];
		server.use(
			nodeInfoRoute(true),
			listRoute(),
			http.post(localApiPath("node/backups/:name/restore"), ({ params }) => {
				restored.push(String(params["name"]));
				return HttpResponse.json({ nodeStopping: true }, { status: 202 });
			}),
		);

		renderCard();
		fireEvent.click(await screen.findByRole("button", { name: `Restore ${snapshotName}` }));

		expect(await screen.findByText("The node is stopping to restore the snapshot. Start the app again.")).toBeTruthy();
		expect(restored).toEqual([snapshotName]);
		expect(fixtures.confirm).toHaveBeenCalledWith(
			expect.objectContaining({ description: expect.stringContaining("database only") }),
		);
		expect((screen.getByRole("button", { name: "Take snapshot" }) as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByRole("button", { name: `Restore ${snapshotName}` }) as HTMLButtonElement).disabled).toBe(true);
	});

	it("does not offer Restore when the node is not in local mode, and says why", async () => {
		server.use(nodeInfoRoute(false), listRoute());

		renderCard();
		const restore = await screen.findByRole("button", { name: `Restore ${snapshotName}` });

		expect(
			await screen.findByText(
				"Restore is available only when the node runs in local mode (the desktop app). This node cannot apply a restore.",
			),
		).toBeTruthy();
		expect((restore as HTMLButtonElement).disabled).toBe(true);
	});

	it("shows the localized local-mode note instead of the node's English refusal when the mode is unknown", async () => {
		server.use(
			problemDetailsRoute("get", "diagnostics/node-info", 500, { title: "boom" }),
			listRoute(),
			problemDetailsRoute("post", "node/backups/:name/restore", 409, {
				title: "A restore is applied only when the node runs in local mode.",
			}),
		);

		renderCard();
		const restore = await screen.findByRole("button", { name: `Restore ${snapshotName}` });
		await waitFor(() => expect((restore as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(restore);

		expect(
			await screen.findByText(
				"Restore is available only when the node runs in local mode (the desktop app). This node cannot apply a restore.",
			),
		).toBeTruthy();
		expect(screen.queryByText("A restore is applied only when the node runs in local mode.")).toBeNull();
		expect((restore as HTMLButtonElement).disabled).toBe(true);
	});
});
