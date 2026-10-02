// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { AppErrorFallback } from "@/AppErrorFallback";
import { SCHEMA_VERSION, type Snapshot } from "@/core/diagnostics/Diagnostics";
import en from "@/locales/en.json";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

const fixtures = vi.hoisted(() => ({
	snapshots: [] as Snapshot[],
	nodeInfo: undefined as Record<string, unknown> | undefined,
	exportSupportBundle: vi.fn((..._args: unknown[]) => Promise.resolve()),
}));

vi.mock("@/features/diagnostics/UseSnapshots", () => ({ useSnapshots: () => ({ data: fixtures.snapshots }) }));
vi.mock("@/features/diagnostics/queries/useNodeInfo", () => ({ useNodeInfo: () => ({ data: fixtures.nodeInfo }) }));
vi.mock("@/features/diagnostics/SupportBundle", () => ({
	exportSupportBundle: (...args: unknown[]) => fixtures.exportSupportBundle(...args),
}));

const strings = en.app.errorFallback;

function snapshot(id: string): Snapshot {
	return {
		id,
		createdAt: 1,
		schemaVersion: SCHEMA_VERSION,
		kind: "error",
		error: { message: "Render exploded", source: "boundary" },
		breadcrumbs: [],
		network: [],
		env: { route: "/", appVersion: "1", userAgent: "t", viewport: { width: 1, height: 1 }, locale: "en" },
	};
}

function renderFallback(onRetry = vi.fn()) {
	renderWithMantine(<AppErrorFallback error={new Error("kaput")} onRetry={onRetry} />);
	return onRetry;
}

describe("AppErrorFallback", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		fixtures.snapshots = [];
		fixtures.nodeInfo = undefined;
		fixtures.exportSupportBundle.mockClear();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders the translated copy and retries", () => {
		const onRetry = renderFallback();

		expect(screen.getByText(strings.alertTitle)).toBeTruthy();
		expect(screen.getByText(strings.title)).toBeTruthy();
		expect(screen.getByText("kaput")).toBeTruthy();
		fireEvent.click(screen.getByRole("button", { name: strings.retry }));
		expect(onRetry).toHaveBeenCalledTimes(1);
	});

	it("disables export with a hint while no snapshot is stored", () => {
		renderFallback();

		expect(screen.getByRole("button", { name: strings.exportDiagnostics }).hasAttribute("disabled")).toBe(true);
		expect(screen.getByText(strings.exportUnavailable)).toBeTruthy();
	});

	it("exports the newest stored snapshot", async () => {
		fixtures.snapshots = [snapshot("newest"), snapshot("older")];
		renderFallback();

		fireEvent.click(screen.getByRole("button", { name: strings.exportDiagnostics }));

		await waitFor(() => expect(fixtures.exportSupportBundle).toHaveBeenCalledTimes(1));
		expect(fixtures.exportSupportBundle.mock.calls[0]?.[0]).toBe(fixtures.snapshots[0]);
		expect(screen.queryByText(strings.exportUnavailable)).toBeNull();
	});

	it("hides the issue link without node info and shows it with one", () => {
		renderFallback();
		expect(screen.queryByRole("link", { name: strings.openIssue })).toBeNull();
		cleanup();

		fixtures.snapshots = [snapshot("s")];
		fixtures.nodeInfo = {
			version: "1.0.0",
			repositoryUrl: "https://github.com/acme/xe",
			osDescription: "Microsoft Windows 10.0.19045",
		};
		renderFallback();

		const href = screen.getByRole("link", { name: strings.openIssue }).getAttribute("href") ?? "";
		expect(href).toContain("https://github.com/acme/xe/issues/new?template=bug_report.yml&title=Render%20exploded");
		expect(href).toContain("os=Windows%2010");
	});
});
