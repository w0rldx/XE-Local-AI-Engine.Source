// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { CUSTOM_TOOL_TIMEOUT_MAX_FALLBACK } from "@/features/customTools/models/CustomToolModels";
import { useCustomToolMaxTimeoutSeconds } from "@/features/customTools/queries/useCustomToolMaxTimeoutSeconds";
import { jsonRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

// The custom-tool form bounds the command timeout by the node setting, not by a constant that goes stale.
describe("useCustomToolMaxTimeoutSeconds", () => {
	it("follows the node's customToolMaxTimeoutSeconds", async () => {
		server.use(jsonRoute("get", "node-settings", { customToolMaxTimeoutSeconds: 1200 }));
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useCustomToolMaxTimeoutSeconds(), { wrapper });

		await waitFor(() => expect(result.current).toBe(1200));
	});

	it("uses the default ceiling until the node reports one", () => {
		server.use(jsonRoute("get", "node-settings", {}));
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useCustomToolMaxTimeoutSeconds(), { wrapper });

		expect(result.current).toBe(CUSTOM_TOOL_TIMEOUT_MAX_FALLBACK);
	});
});
