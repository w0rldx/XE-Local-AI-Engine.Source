// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { generatedMock } = vi.hoisted(() => ({
	generatedMock: {
		getNodeSettingsOptions: vi.fn(),
		getGraphWorkflowCapabilityOptions: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>();
	return {
		...actual,
		getNodeSettingsOptions: generatedMock.getNodeSettingsOptions,
		getGraphWorkflowCapabilityOptions: generatedMock.getGraphWorkflowCapabilityOptions,
	};
});

vi.mock("@/core/api/ResponseValidation", () => ({
	withResponseValidation: <T,>(options: T): T => options,
}));

import { useServerDisabledNavigationCapabilities } from "@/core/layout/models/NavigationMenuData";

function wrapper({ children }: { children: ReactNode }) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

// Each source either answers with `data` or fails (`undefined` = the request rejects, as a non-operator's settings read does).
function arrange(settings: Record<string, unknown> | undefined, graphEnabled: boolean | undefined): void {
	const settingsFn = vi.fn(() => (settings === undefined ? Promise.reject(new Error("403")) : Promise.resolve(settings)));
	const graphFn = vi.fn(() =>
		graphEnabled === undefined ? Promise.reject(new Error("boom")) : Promise.resolve({ enabled: graphEnabled }),
	);
	generatedMock.getNodeSettingsOptions.mockReturnValue({ queryKey: ["getNodeSettings"], queryFn: settingsFn });
	generatedMock.getGraphWorkflowCapabilityOptions.mockReturnValue({ queryKey: ["getGraphCapability"], queryFn: graphFn });
}

describe("useServerDisabledNavigationCapabilities", () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	it("hides every nav capability whose feature switch is off", async () => {
		arrange(
			{
				developmentEnabled: false,
				workSessionsEnabled: false,
				devWorkflowsEnabled: false,
				graphWorkflowsEnabled: false,
				externalAppsEnabled: false,
				transcriptionEnabled: false,
				schedulerEnabled: false,
				computeEnabled: false,
				agentHomeEnabled: false,
			},
			true,
		);
		const { result } = renderHook(() => useServerDisabledNavigationCapabilities(), { wrapper });

		await waitFor(() =>
			expect([...result.current].sort()).toEqual(
				["development", "devWorkflows", "externalApps", "graphWorkflows", "scheduler", "transcription", "workSessions"].sort(),
			),
		);
	});

	it("hides only the switch that is off", async () => {
		arrange({ developmentEnabled: true, schedulerEnabled: false, workSessionsEnabled: true }, true);
		const { result } = renderHook(() => useServerDisabledNavigationCapabilities(), { wrapper });

		await waitFor(() => expect([...result.current]).toEqual(["scheduler"]));
	});

	it("shows everything when both reads fail", async () => {
		arrange(undefined, undefined);
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
		const { result } = renderHook(() => useServerDisabledNavigationCapabilities(), {
			wrapper: ({ children }: { children: ReactNode }) => (
				<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
			),
		});

		// Gate on both queries having settled as errors, so "nothing hidden" is the answer after the failures, not before.
		await waitFor(() => {
			expect(queryClient.getQueryState(["getNodeSettings"])?.status).toBe("error");
			expect(queryClient.getQueryState(["getGraphCapability"])?.status).toBe("error");
		});
		expect([...result.current]).toEqual([]);
	});

	it("hides graph workflows from the capability probe when the settings read fails", async () => {
		arrange(undefined, false);
		const { result } = renderHook(() => useServerDisabledNavigationCapabilities(), { wrapper });

		await waitFor(() => expect([...result.current]).toEqual(["graphWorkflows"]));
	});

	it("hides graph workflows from the settings switch while the probe still says on", async () => {
		arrange({ graphWorkflowsEnabled: false }, true);
		const { result } = renderHook(() => useServerDisabledNavigationCapabilities(), { wrapper });

		await waitFor(() => expect([...result.current]).toEqual(["graphWorkflows"]));
	});
});
