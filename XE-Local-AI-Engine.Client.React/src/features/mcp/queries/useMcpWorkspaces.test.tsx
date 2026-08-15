// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";

import { useCreateMcpWorkspace, useDeleteMcpWorkspace, useMcpWorkspaces } from "@/features/mcp/queries/useMcpWorkspaces";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

// These hooks are thin by design — the generated `*Options()`/`*Mutation()` factory supplies the URL, the query key
// and the response validator, and the hook adds only a `select` mapping and an invalidation. Mocking the generated
// module (as this suite used to) replaced exactly those supplied parts with test-authored fakes, leaving the query
// key, the request URL and the zod response contract unasserted. Serving the routes over MSW instead keeps the whole
// generated layer in the test, so the invalidation is asserted by observing the LIST REFETCH it causes rather than by
// spying on `queryClient.invalidateQueries`.

const workspacePath = "workspaces";

interface WorkspaceRow {
	workspaceId: string;
	alias: string;
	mode: string;
}

/** Serves the workspace collection out of a mutable array and counts list reads, so a refetch is observable. */
function workspaceRoutes(initial: WorkspaceRow[]) {
	const rows = [...initial];
	const state = { listReads: 0, createdBody: undefined as unknown, deletedUrl: "" };

	server.use(
		http.get(localApiPath(workspacePath), () => {
			state.listReads += 1;
			return HttpResponse.json({ items: rows });
		}),
		http.post(localApiPath(workspacePath), async ({ request }) => {
			state.createdBody = await request.json();
			const created: WorkspaceRow = { workspaceId: "ws_created", alias: "Second", mode: "read-only" };
			rows.push(created);
			return HttpResponse.json(created);
		}),
		http.delete(localApiPath(`${workspacePath}/:workspaceId`), ({ request, params }) => {
			state.deletedUrl = request.url;
			const index = rows.findIndex((row) => row.workspaceId === params["workspaceId"]);
			if (index >= 0) {
				rows.splice(index, 1);
			}
			return new HttpResponse(null, { status: 204 });
		}),
	);

	return state;
}

const seed: WorkspaceRow[] = [{ workspaceId: "ws_opaque", alias: "Repository", mode: "read-only" }];

describe("MCP workspace queries over the real client", () => {
	it("maps the served response to an opaque read-only workspace model", async () => {
		workspaceRoutes(seed);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useMcpWorkspaces(), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data).toEqual([{ id: "ws_opaque", alias: "Repository", mode: "read-only" }]);
	});

	it("create posts the alias and host path, then refetches the list", async () => {
		const state = workspaceRoutes(seed);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => ({ list: useMcpWorkspaces(), create: useCreateMcpWorkspace() }), { wrapper });
		await waitFor(() => expect(result.current.list.isSuccess).toBe(true));
		expect(state.listReads).toBe(1);

		result.current.create.mutate({ body: { alias: "Second", hostPath: "/trusted/repository" } });

		await waitFor(() => expect(result.current.create.isSuccess).toBe(true));
		expect(state.createdBody).toEqual({ alias: "Second", hostPath: "/trusted/repository" });
		// The invalidation is only real if the list actually re-reads and shows the new row.
		await waitFor(() => expect(result.current.list.data).toHaveLength(2));
		expect(state.listReads).toBe(2);
	});

	it("delete addresses the opaque workspace id in the route, then refetches the list", async () => {
		const state = workspaceRoutes(seed);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => ({ list: useMcpWorkspaces(), remove: useDeleteMcpWorkspace() }), { wrapper });
		await waitFor(() => expect(result.current.list.isSuccess).toBe(true));

		result.current.remove.mutate({ path: { workspaceId: "ws_opaque" } });

		await waitFor(() => expect(result.current.remove.isSuccess).toBe(true));
		expect(state.deletedUrl).toContain(localApiPath("workspaces/ws_opaque"));
		await waitFor(() => expect(result.current.list.data).toEqual([]));
	});
});
