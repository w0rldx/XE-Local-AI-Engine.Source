// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsDiagnosticsV1NodeInfoResponse } from "@/core/api/generated";
import { buildIssueUrl } from "@/features/diagnostics/IssueUrl";
import { useNodeInfo } from "@/features/diagnostics/queries/useNodeInfo";
import { jsonRoute } from "@/test/msw/Handlers";
import { setupMswServer } from "@/test/UseMswServer";

const server = setupMswServer();

const nodeInfo: XeLocalAiEngineClientEndpointsDiagnosticsV1NodeInfoResponse = {
	capturedAtUtc: "2026-10-02T08:00:00+00:00",
	version: "1.0.0-rc.3+abc1234",
	commit: "abc1234",
	flavour: "tester",
	selectedChannel: "preview",
	defaultChannel: "preview",
	repositoryUrl: "https://github.com/acme/xe",
	isLocalMode: true,
	isShellOwned: false,
	osDescription: "Ubuntu 24.04.1 LTS",
	osArchitecture: "X64",
	processArchitecture: "X64",
	runtimeFramework: ".NET 10.0.0",
	cpuModel: "AMD Ryzen 9 7950X",
	cpuCores: 16,
	totalRamBytes: 64 * 1024 ** 3,
	availableRamBytes: 32 * 1024 ** 3,
	freeDiskBytes: null,
	gpuVendor: "Nvidia",
	inferenceBackend: "Cuda",
	cpuFallback: false,
	gpus: [{ name: "NVIDIA GeForce RTX 5090", totalBytes: 32 * 1024 ** 3, freeBytes: null }],
	liveGpuMemory: null,
	runtimes: null,
	residents: null,
	models: null,
	runningModels: null,
	settings: null,
	uptimeSeconds: 42,
	warnings: [],
};

function wrapper({ children }: { children: ReactNode }) {
	return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>;
}

describe("useNodeInfo", () => {
	it("reads the node-info report through the generated client", async () => {
		server.use(jsonRoute("get", "diagnostics/node-info", nodeInfo));

		const { result } = renderHook(() => useNodeInfo(), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data).toEqual(nodeInfo);
		expect(buildIssueUrl(result.current.data as typeof nodeInfo, undefined, "x")).toBe(
			"https://github.com/acme/xe/issues/new?template=bug_report.yml&title=Problem%20report&version=1.0.0-rc.3%2Babc1234&os=Linux&hardware=CPU%3A%20AMD%20Ryzen%209%207950X%20(16%20cores)%20%2F%20GPU%3A%20NVIDIA%20GeForce%20RTX%205090%20(32%20GB)%20%2F%20RAM%3A%2064%20GB",
		);
	});
});
