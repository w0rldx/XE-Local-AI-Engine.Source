// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import "@/i18n";

import type { Snapshot } from "@/core/diagnostics/Diagnostics";

// The Diagnostics panel consumes the snapshot store's data hooks and bundler; mock those seams so the panel renders without
// IndexedDB or a live capture. `vi.hoisted` keeps the mutable fixtures available inside the hoisted
// `vi.mock` factories.
const fixtures = vi.hoisted(() => ({
	snapshots: [] as Snapshot[],
	isLoading: false,
	isError: false,
	captureSnapshot: vi.fn((..._args: unknown[]) => Promise.resolve({})),
	exportSupportBundle: vi.fn((..._args: unknown[]) => Promise.resolve()),
	nodeInfo: undefined as Record<string, unknown> | undefined,
	importMutate: vi.fn(),
	deleteMutate: vi.fn(),
	clearMutate: vi.fn(),
	confirm: vi.fn(() => Promise.resolve(true)),
}));

vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: fixtures.confirm }),
}));

vi.mock("@/features/diagnostics/BuildSnapshot", () => ({
	captureSnapshot: (...args: unknown[]) => fixtures.captureSnapshot(...args),
}));

vi.mock("@/features/diagnostics/SupportBundle", () => ({
	exportSupportBundle: (...args: unknown[]) => fixtures.exportSupportBundle(...args),
}));

vi.mock("@/features/diagnostics/queries/useNodeInfo", () => ({
	useNodeInfo: () => ({ data: fixtures.nodeInfo }),
}));

vi.mock("@/features/diagnostics/UseSnapshots", () => ({
	useSnapshots: () => ({ data: fixtures.snapshots, isLoading: fixtures.isLoading, isError: fixtures.isError }),
	useDeleteSnapshot: () => ({ mutate: fixtures.deleteMutate, isPending: false, variables: undefined }),
	useClearSnapshots: () => ({ mutate: fixtures.clearMutate, isPending: false }),
	useImportSnapshot: () => ({ mutate: fixtures.importMutate, isPending: false }),
}));

import { DiagnosticsPanel } from "@/features/diagnostics/components/DiagnosticsPanel";
import { testMantineTheme } from "@/test/MantineTestRender";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { setupMswServer } from "@/test/UseMswServer";

// The header reads the node's log level and the database-snapshots card its list on every render; both are ambient to
// the snapshot tests, so they are file defaults.
const server = setupMswServer(
	jsonRoute("get", "diagnostics/log-level", { verbose: false }),
	jsonRoute("get", "node/backups", { backups: [], lastAutomaticBackup: { outcome: "NotRun" } }),
);

function makeSnapshot(overrides: Partial<Snapshot> = {}): Snapshot {
	return {
		id: "snap-1",
		createdAt: Date.now(),
		schemaVersion: 1,
		kind: "error",
		error: { message: "Boom happened", source: "boundary" },
		breadcrumbs: [],
		network: [],
		env: { route: "/chat", appVersion: "1.0.0", userAgent: "test", viewport: { width: 800, height: 600 }, locale: "en" },
		...overrides,
	};
}

function renderPanel(ui: ReactElement) {
	return render(
		<QueryClientProvider client={new QueryClient()}>
			<MantineProvider env="test" theme={testMantineTheme}>
				{ui}
			</MantineProvider>
		</QueryClientProvider>,
	);
}

describe("DiagnosticsPanel", () => {
	beforeEach(() => {
		fixtures.snapshots = [];
		fixtures.isLoading = false;
		fixtures.isError = false;
		fixtures.captureSnapshot.mockClear();
		fixtures.importMutate.mockClear();
		fixtures.exportSupportBundle.mockClear();
		fixtures.nodeInfo = undefined;
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class {
				observe = vi.fn();
				unobserve = vi.fn();
				disconnect = vi.fn();
			},
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a row per snapshot from useSnapshots", () => {
		fixtures.snapshots = [
			makeSnapshot({
				id: "a",
				error: { message: "First error", source: "boundary" },
				env: { route: "/chat", appVersion: "1", userAgent: "t", viewport: { width: 1, height: 1 }, locale: "en" },
			}),
			makeSnapshot({
				id: "b",
				kind: "manual",
				error: undefined,
				env: { route: "/models", appVersion: "1", userAgent: "t", viewport: { width: 1, height: 1 }, locale: "en" },
			}),
		];

		renderPanel(<DiagnosticsPanel />);

		expect(screen.getByText("First error")).toBeTruthy();
		expect(screen.getByText("/chat")).toBeTruthy();
		expect(screen.getByText("/models")).toBeTruthy();
	});

	it("shows the empty state when there are no snapshots", () => {
		fixtures.snapshots = [];

		renderPanel(<DiagnosticsPanel />);

		expect(screen.getByText("No snapshots yet")).toBeTruthy();
	});

	it("triggers a manual capture when Report a problem is clicked", async () => {
		renderPanel(<DiagnosticsPanel />);

		fireEvent.click(screen.getByText("Report a problem"));

		await waitFor(() => expect(fixtures.captureSnapshot).toHaveBeenCalledWith("manual"));
	});

	it("imports a selected file through useImportSnapshot", () => {
		const { container } = renderPanel(<DiagnosticsPanel />);

		const input = container.querySelector('input[type="file"]');
		expect(input).toBeTruthy();
		const file = new File(["data"], "snapshot.zip", { type: "application/zip" });
		fireEvent.change(input as HTMLInputElement, { target: { files: [file] } });

		expect(fixtures.importMutate).toHaveBeenCalledTimes(1);
		expect(fixtures.importMutate.mock.calls[0]?.[0]).toBe(file);
	});

	// The Import button is the labelled control; the input behind it is display:none and only opens the OS picker, so
	// it belongs out of the accessibility tree rather than in it as a nameless file field.
	it("keeps the hidden import input out of the accessibility tree", () => {
		const { container } = renderPanel(<DiagnosticsPanel />);

		const input = container.querySelector('input[type="file"]');
		expect(input?.getAttribute("aria-hidden")).toBe("true");
		expect(input?.getAttribute("tabindex")).toBe("-1");
	});

	it("exports the merged support bundle for the row's snapshot", async () => {
		const snapshot = makeSnapshot({ id: "row-1" });
		fixtures.snapshots = [snapshot];

		renderPanel(<DiagnosticsPanel />);
		fireEvent.click(screen.getByRole("button", { name: "Export" }));

		await waitFor(() => expect(fixtures.exportSupportBundle).toHaveBeenCalledTimes(1));
		expect(fixtures.exportSupportBundle.mock.calls[0]?.[0]).toBe(snapshot);
	});

	it("links a prefilled GitHub issue in a new tab once node info is known", () => {
		fixtures.snapshots = [makeSnapshot()];
		fixtures.nodeInfo = { version: "1.2.3", repositoryUrl: "https://github.com/acme/xe", osDescription: "Ubuntu 24.04 LTS" };

		renderPanel(<DiagnosticsPanel />);

		const link = screen.getByRole("link", { name: "Open GitHub issue" });
		expect(link.getAttribute("href")).toMatch(
			/^https:\/\/github\.com\/acme\/xe\/issues\/new\?template=bug_report\.yml&title=Boom%20happened&version=1\.2\.3&os=Linux/,
		);
		expect(link.getAttribute("target")).toBe("_blank");
		expect(link.getAttribute("rel")).toContain("noopener");
	});

	it("hides the issue link while node info is unavailable", () => {
		renderPanel(<DiagnosticsPanel />);

		expect(screen.queryByRole("link", { name: "Open GitHub issue" })).toBeNull();
	});

	it("shows the verbose-logging switch from the node and PUTs the toggled value", async () => {
		const bodies: unknown[] = [];
		server.use(
			http.put(localApiPath("diagnostics/log-level"), async ({ request }) => {
				bodies.push(await request.json());
				return HttpResponse.json({ verbose: true });
			}),
		);

		renderPanel(<DiagnosticsPanel />);

		const toggle = screen.getByRole("switch", { name: "Verbose logging until restart" });
		await waitFor(() => expect((toggle as HTMLInputElement).disabled).toBe(false));
		expect((toggle as HTMLInputElement).checked).toBe(false);

		fireEvent.click(toggle);

		await waitFor(() => expect(bodies).toEqual([{ verbose: true }]));
	});
});
