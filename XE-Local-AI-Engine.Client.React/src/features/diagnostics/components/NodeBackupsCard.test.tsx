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
import { localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
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
		server.use(listRoute({ outcome: "Failed", atUtc: "2026-10-08T11:59:00Z", error: "disk full" }));

		renderCard();

		expect(await screen.findByText(snapshotName)).toBeTruthy();
		expect(screen.getByText("2 MB")).toBeTruthy();
		expect(screen.getByText(/Automatic backup at this start: failed .*disk full/)).toBeTruthy();
	});

	it("takes a snapshot and refreshes the list", async () => {
		let posts = 0;
		server.use(
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
});
