// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import "@/i18n";

import type { Snapshot } from "@/core/diagnostics/Diagnostics";

// Lane C panel consumes Lane B's data hooks and bundler; mock those seams so the panel renders without
// IndexedDB or a live capture. `vi.hoisted` keeps the mutable fixtures available inside the hoisted
// `vi.mock` factories.
const fixtures = vi.hoisted(() => ({
	snapshots: [] as Snapshot[],
	isLoading: false,
	isError: false,
	captureSnapshot: vi.fn((..._args: unknown[]) => Promise.resolve({})),
	exportSnapshot: vi.fn((..._args: unknown[]) => undefined),
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

vi.mock("@/features/diagnostics/ExportSnapshot", () => ({
	exportSnapshot: (...args: unknown[]) => fixtures.exportSnapshot(...args),
}));

vi.mock("@/features/diagnostics/UseSnapshots", () => ({
	useSnapshots: () => ({ data: fixtures.snapshots, isLoading: fixtures.isLoading, isError: fixtures.isError }),
	useDeleteSnapshot: () => ({ mutate: fixtures.deleteMutate, isPending: false, variables: undefined }),
	useClearSnapshots: () => ({ mutate: fixtures.clearMutate, isPending: false }),
	useImportSnapshot: () => ({ mutate: fixtures.importMutate, isPending: false }),
}));

import { DiagnosticsPanel } from "@/features/diagnostics/components/DiagnosticsPanel";

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
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("DiagnosticsPanel", () => {
	beforeEach(() => {
		fixtures.snapshots = [];
		fixtures.isLoading = false;
		fixtures.isError = false;
		fixtures.captureSnapshot.mockClear();
		fixtures.importMutate.mockClear();
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
});
