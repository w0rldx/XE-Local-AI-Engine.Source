// @vitest-environment jsdom

import { isRedirect } from "@tanstack/react-router";
import { describe, expect, it, vi } from "vitest";

// The page itself is CloudProviders.test.tsx's subject; the guards never render it.
vi.mock("@/features/cloud-providers/pages/CloudProviders", () => ({ CloudProviders: () => null }));

import { Route as CloudProvidersRoute } from "@/routes/_layout/cloud-providers";
import { Route as CloudSettingsRoute } from "@/routes/_layout/cloud-settings";
import { Route as ExternalProvidersRoute } from "@/routes/_layout/external-providers";

async function runGuard(route: { options: { beforeLoad?: unknown } }): Promise<unknown> {
	const beforeLoad = route.options.beforeLoad;
	if (beforeLoad === undefined) {
		throw new Error("the route declares no beforeLoad");
	}
	try {
		// biome-ignore lint/suspicious/noExplicitAny: these guards read nothing off the router's argument.
		await (beforeLoad as any)({});
		return undefined;
	} catch (thrown) {
		return thrown;
	}
}

describe("cloud provider routes", () => {
	it.each([
		["/cloud-settings", CloudSettingsRoute],
		["/external-providers", ExternalProvidersRoute],
	])("redirects the former %s page to /cloud-providers", async (_path, route) => {
		const thrown = await runGuard(route);

		expect(isRedirect(thrown)).toBe(true);
		expect((thrown as { options?: { to?: string } }).options?.to).toBe("/cloud-providers");
	});

	it("lets the operator through while a provider capability is on", async () => {
		await expect(runGuard(CloudProvidersRoute)).resolves.toBeUndefined();
	});

	it("redirects home when both provider capabilities are off", async () => {
		vi.resetModules();
		vi.doMock("@/capabilities/NodeCapabilities", async () => {
			const actual = await vi.importActual<typeof import("@/capabilities/NodeCapabilities")>("@/capabilities/NodeCapabilities");
			return { ...actual, nodeCapabilities: { ...actual.nodeCapabilities, cloudSettings: false, externalProviders: false } };
		});
		const { Route } = await import("@/routes/_layout/cloud-providers");

		const thrown = await runGuard(Route);

		expect(isRedirect(thrown)).toBe(true);
		expect((thrown as { options?: { to?: string } }).options?.to).toBe("/");
		vi.doUnmock("@/capabilities/NodeCapabilities");
	});
});
