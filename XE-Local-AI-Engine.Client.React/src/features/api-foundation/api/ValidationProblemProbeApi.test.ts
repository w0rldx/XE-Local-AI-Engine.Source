import { describe, expect, it, vi } from "vitest";

const { probeEndpoint } = vi.hoisted(() => ({
	probeEndpoint: vi.fn(),
}));

vi.mock("@/core/api/generated", () => ({
	xeLocalAiEngineClientEndpointsApiFoundationV1ValidationProblemProbeEndpoint: probeEndpoint,
}));

import { probeLocalApi } from "@/features/api-foundation/api/ValidationProblemProbeApi";

describe("probeLocalApi", () => {
	it("calls the generated local API client", async () => {
		probeEndpoint.mockResolvedValue({ data: { name: "operator" } });

		await expect(probeLocalApi("operator")).resolves.toEqual({ name: "operator" });
		expect(probeEndpoint).toHaveBeenCalledWith({
			body: { name: "operator" },
			throwOnError: true,
		});
	});
});
