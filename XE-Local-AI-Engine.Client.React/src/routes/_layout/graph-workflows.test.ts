// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { isRedirect } from "@tanstack/react-router";
import { describe, expect, it, vi } from "vitest";

import { getGraphWorkflowCapabilityQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";

// The page itself is GraphWorkflowsPage.test.tsx's subject; the guard never renders it.
vi.mock("@/features/graphWorkflows/pages/GraphWorkflowsPage", () => ({ GraphWorkflowsPage: () => null }));

import { Route } from "@/routes/_layout/graph-workflows";

async function runGuard(queryClient: QueryClient): Promise<unknown> {
	const beforeLoad = Route.options.beforeLoad;
	if (beforeLoad === undefined) {
		throw new Error("the graph-workflows route declares no beforeLoad");
	}
	// biome-ignore lint/suspicious/noExplicitAny: the guard reads only `context` off the router's argument.
	return await (beforeLoad as any)({ context: { queryClient } }).then(
		() => undefined,
		(thrown: unknown) => thrown,
	);
}

function clientWith(enabled: boolean | undefined): QueryClient {
	const queryClient = new QueryClient({
		defaultOptions: {
			queries: {
				retry: false,
				queryFn: async () => {
					throw new Error("capability unavailable");
				},
			},
		},
	});
	if (enabled !== undefined) {
		queryClient.setQueryData(getGraphWorkflowCapabilityQueryKey(), { enabled });
	}
	return queryClient;
}

describe("graph-workflows route guard", () => {
	it("redirects home when the node switched graph workflows off", async () => {
		const thrown = await runGuard(clientWith(false));

		expect(isRedirect(thrown)).toBe(true);
		expect((thrown as { options?: { to?: string } }).options?.to).toBe("/");
	});

	it("lets the operator through when the feature is on", async () => {
		await expect(runGuard(clientWith(true))).resolves.toBeUndefined();
	});

	it("fails open when the capability cannot be read", async () => {
		// A read failure must not lock the page away; the page reports its own load errors.
		await expect(runGuard(clientWith(undefined))).resolves.toBeUndefined();
	});
});
