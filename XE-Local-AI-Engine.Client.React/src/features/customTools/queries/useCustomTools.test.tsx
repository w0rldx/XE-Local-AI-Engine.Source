// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";

import {
	useCreateCustomTool,
	useCustomTool,
	useCustomTools,
	useDeleteCustomTool,
	useUpdateCustomTool,
} from "@/features/customTools/queries/useCustomTools";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

// Served over MSW rather than mocked at the generated module, so the generated URLs, query keys and zod response
// validators stay in the test. The two invalidation rules these hooks add are asserted by the REFETCHES they cause:
// every mutation refreshes the list, and an update additionally refreshes every open single-tool cache (the partial
// `_id: "getCustomTool"` key match) so a re-opened editor cannot show stale values.

const toolsPath = "custom-tools";
const toolId = "3a1f0f0e-0000-4000-8000-000000000001";

/** A full CustomToolView row — every field the generated zod response validator requires. */
function toolRow(overrides: Record<string, unknown> = {}) {
	return {
		id: toolId,
		name: "custom__fetch_status",
		description: "Fetches a status page.",
		kind: "HttpFetch",
		mode: "Fixed",
		enabled: true,
		acknowledged: true,
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		parameters: [{ name: "city", type: "string", description: "City", required: true }],
		http: {
			method: "GET",
			urlTemplate: "https://example.test/status",
			headers: [{ name: "X-Key", value: "__secret_set__", isSecret: true }],
			bodyTemplate: null,
			allowedHosts: ["example.test"],
		},
		command: null,
		...overrides,
	};
}

function customToolRoutes() {
	const state = { listReads: 0, singleReads: 0, sentBody: undefined as unknown, deletedUrl: "" };
	let row = toolRow();

	server.use(
		http.get(localApiPath(toolsPath), () => {
			state.listReads += 1;
			return HttpResponse.json({ items: [row] });
		}),
		http.get(localApiPath(`${toolsPath}/:customToolId`), () => {
			state.singleReads += 1;
			return HttpResponse.json(row);
		}),
		http.post(localApiPath(toolsPath), async ({ request }) => {
			state.sentBody = await request.json();
			return HttpResponse.json(row);
		}),
		http.put(localApiPath(`${toolsPath}/:customToolId`), async ({ request }) => {
			state.sentBody = await request.json();
			row = toolRow({ description: "Updated.", version: 4 });
			return HttpResponse.json(row);
		}),
		http.delete(localApiPath(`${toolsPath}/:customToolId`), ({ request }) => {
			state.deletedUrl = request.url;
			return new HttpResponse(null, { status: 204 });
		}),
	);

	return state;
}

describe("custom tool queries over the real client", () => {
	it("maps the list response into the domain view model", async () => {
		customToolRoutes();
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useCustomTools(), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data).toHaveLength(1);
		expect(result.current.data?.[0]).toMatchObject({
			id: toolId,
			name: "custom__fetch_status",
			kind: "HttpFetch",
			mode: "Fixed",
			enabled: true,
			version: 3,
			command: null,
		});
		// A masked secret rides straight through: the backend resolves an unchanged sentinel back to the stored value.
		expect(result.current.data?.[0]?.http?.headers[0]).toEqual({ name: "X-Key", value: "__secret_set__", isSecret: true });
	});

	// The single-tool read exists only for the editor, so it must stay off the wire until a tool is actually opened.
	it("does not fetch a single tool until an id is supplied", async () => {
		const state = customToolRoutes();
		const { wrapper } = createProvidersWrapper();

		const { result, rerender } = renderHook(({ id }: { id: string | null }) => useCustomTool(id), {
			wrapper,
			initialProps: { id: null as string | null },
		});

		expect(result.current.fetchStatus).toBe("idle");
		expect(state.singleReads).toBe(0);

		rerender({ id: toolId });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(state.singleReads).toBe(1);
		expect(result.current.data?.id).toBe(toolId);
	});

	it("create posts the definition body and refetches the list", async () => {
		const state = customToolRoutes();
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => ({ list: useCustomTools(), create: useCreateCustomTool() }), { wrapper });
		await waitFor(() => expect(result.current.list.isSuccess).toBe(true));
		expect(state.listReads).toBe(1);

		result.current.create.mutate({ body: { name: "fetch_status", kind: "HttpFetch", mode: "Fixed", acknowledged: true } });

		await waitFor(() => expect(result.current.create.isSuccess).toBe(true));
		expect(state.sentBody).toEqual({ name: "fetch_status", kind: "HttpFetch", mode: "Fixed", acknowledged: true });
		await waitFor(() => expect(state.listReads).toBe(2));
	});

	// An edit must refresh BOTH caches — the list and every open single-tool query — or a re-opened editor shows the
	// pre-edit values.
	it("update refetches the list and the open single-tool cache", async () => {
		const state = customToolRoutes();
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(
			() => ({ list: useCustomTools(), single: useCustomTool(toolId), update: useUpdateCustomTool() }),
			{ wrapper },
		);
		await waitFor(() => expect(result.current.single.isSuccess).toBe(true));
		expect(state.singleReads).toBe(1);

		result.current.update.mutate({ path: { customToolId: toolId }, body: { description: "Updated." } });

		await waitFor(() => expect(result.current.update.isSuccess).toBe(true));
		await waitFor(() => expect(result.current.single.data?.description).toBe("Updated."));
		expect(state.listReads).toBe(2);
		expect(state.singleReads).toBe(2);
	});

	it("delete addresses the tool id in the route and refetches the list", async () => {
		const state = customToolRoutes();
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => ({ list: useCustomTools(), remove: useDeleteCustomTool() }), { wrapper });
		await waitFor(() => expect(result.current.list.isSuccess).toBe(true));

		result.current.remove.mutate({ path: { customToolId: toolId } });

		await waitFor(() => expect(result.current.remove.isSuccess).toBe(true));
		expect(state.deletedUrl).toContain(localApiPath(`custom-tools/${toolId}`));
		await waitFor(() => expect(state.listReads).toBe(2));
	});
});
