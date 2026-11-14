// @vitest-environment jsdom

// Run history is paged by the server, so the next page is a query with no data of its own yet. Holding the previous
// page keeps the pager's total while it loads; holding the OLD filter's runs would show them under the new filter
// until its fetch lands.

import { renderHook, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it } from "vitest";

import type { ScheduledJobRunFilters } from "@/features/scheduler/models/SchedulerModels";
import { useScheduledJobRuns } from "@/features/scheduler/queries/useScheduler";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

function run(id: string, status: string) {
	return {
		id,
		scheduledJobId: "aaaaaaaa-0000-4000-8000-000000000001",
		templateId: "cleanup",
		triggeredBy: "Schedule",
		status,
		createdAtUtc: 1,
	};
}

describe("useScheduledJobRuns", () => {
	it("holds the previous page of the same filters while the next loads, but never another filter's runs", async () => {
		// Only the unfiltered first page answers; every other request waits until the test is done looking at it.
		let release = (): void => undefined;
		const held = new Promise<void>((resolve) => {
			release = resolve;
		});
		server.use(
			http.get(localApiPath("scheduler/runs"), async ({ request }) => {
				const query = new URL(request.url).searchParams;
				const status = query.get("status");
				if (status !== null || query.get("offset") !== "0") {
					await held;
				}
				return HttpResponse.json({
					items: [run(`bbbbbbbb-0000-4000-8000-00000000000${status === null ? 1 : 2}`, status ?? "Succeeded")],
					totalCount: 60,
				});
			}),
		);
		const { wrapper } = createProvidersWrapper();
		const { result, rerender } = renderHook(
			({ filters, offset }: { filters: ScheduledJobRunFilters; offset: number }) =>
				useScheduledJobRuns(filters, { limit: 25, offset }),
			{ wrapper, initialProps: { filters: {}, offset: 0 } },
		);
		await waitFor(() => expect(result.current.data?.items[0]?.status).toBe("Succeeded"));

		rerender({ filters: {}, offset: 25 });
		expect(result.current.isPlaceholderData).toBe(true);
		expect(result.current.data?.totalCount).toBe(60);

		rerender({ filters: { status: "Failed" }, offset: 0 });
		expect(result.current.data).toBeUndefined();

		release();
		await waitFor(() => expect(result.current.data?.items[0]?.status).toBe("Failed"));
	});
});
