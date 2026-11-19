// @vitest-environment jsdom

// The sample review does not poll and the hub resync refreshes only the dataset list, so a dataset mutation is the
// only thing that tells an open review its rows and total moved. The old `["training", "datasets"]` prefix reached every
// dataset's sample pages; the generated keys have to reach them explicitly.

import { QueryClient } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { t } from "i18next";
import { HttpResponse, http } from "msw";
import { describe, expect, it } from "vitest";

import {
	listToolMocksQueryKey,
	listTrainingDatasetsQueryKey,
	listTrainingDefinitionsQueryKey,
	listTrainingSamplesQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { useDatasetBuildOperations } from "@/core/operations/hooks/useJobOperations";
import { useCancelTrainingDataset, useTrainingDatasets } from "@/features/training/queries/useTrainingDatasets";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const datasetId = "aaaaaaaa-0000-4000-8000-000000000001";
const otherDatasetId = "bbbbbbbb-0000-4000-8000-000000000002";

describe("training dataset invalidation", () => {
	it("cancelling a generation invalidates the dataset list and every sample page, but not definitions or mocks", async () => {
		server.use(http.post(localApiPath(`training/datasets/${datasetId}/cancel`), () => new HttpResponse(null, { status: 204 })));
		// Seeded entries have no observer, so they must outlive the test client's zero `gcTime` to be observable at all.
		const queryClient = new QueryClient({
			defaultOptions: { queries: { retry: false, gcTime: Number.POSITIVE_INFINITY } },
		});
		const { wrapper } = createProvidersWrapper({ queryClient });
		const invalidated = {
			list: listTrainingDatasetsQueryKey(),
			firstPage: listTrainingSamplesQueryKey({ path: { datasetId }, query: { page: 1, pageSize: 20 } }),
			filteredPage: listTrainingSamplesQueryKey({
				path: { datasetId },
				query: { page: 2, pageSize: 20, reviewState: "Approved" },
			}),
			otherDataset: listTrainingSamplesQueryKey({ path: { datasetId: otherDatasetId }, query: { page: 1, pageSize: 20 } }),
		};
		const kept = { definitions: listTrainingDefinitionsQueryKey(), mocks: listToolMocksQueryKey() };
		for (const key of [...Object.values(invalidated), ...Object.values(kept)]) {
			queryClient.setQueryData(key, {});
		}

		const { result } = renderHook(() => useCancelTrainingDataset(), { wrapper });
		result.current.mutate({ datasetId });
		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		for (const key of Object.values(invalidated)) {
			expect(queryClient.getQueryState(key)?.isInvalidated).toBe(true);
		}
		for (const key of Object.values(kept)) {
			expect(queryClient.getQueryState(key)?.isInvalidated).toBe(false);
		}
	});
});

describe("training dataset list cache", () => {
	it("serves the datasets page and the operations feed from one request and one raw cache entry", async () => {
		let requests = 0;
		const raw = {
			items: [
				{
					id: datasetId,
					definitionId: "cccccccc-0000-4000-8000-000000000003",
					definitionVersion: 1,
					name: "Support",
					status: "Generating",
					revision: 1,
					totalSampleCount: 0,
					goodSampleCount: 0,
					badSampleCount: 0,
					rejectedSampleCount: 0,
					duplicateSampleCount: 0,
					workStatus: "Running",
					version: 1,
					createdAtUtc: 1,
					updatedAtUtc: 1,
				},
			],
		};
		server.use(
			http.get(localApiPath("training/datasets"), () => {
				requests += 1;
				return HttpResponse.json(raw);
			}),
		);
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
		const { wrapper } = createProvidersWrapper({ queryClient });

		const { result } = renderHook(
			() => ({ page: useTrainingDatasets(), feed: useDatasetBuildOperations({ enabled: true, watch: false, t }) }),
			{ wrapper },
		);

		await waitFor(() => expect(result.current.page.data).toHaveLength(1));
		expect(result.current.page.data?.[0]).toMatchObject({ id: datasetId, name: "Support", status: "Generating" });
		expect(result.current.feed).toEqual([expect.objectContaining({ id: `dataset-build:${datasetId}`, state: "running" })]);
		expect(requests).toBe(1);
		const entries = queryClient.getQueryCache().findAll({ queryKey: listTrainingDatasetsQueryKey() });
		expect(entries).toHaveLength(1);
		expect(entries[0]?.state.data).toMatchObject({ items: [expect.objectContaining({ id: datasetId, workStatus: "Running" })] });
	});
});
