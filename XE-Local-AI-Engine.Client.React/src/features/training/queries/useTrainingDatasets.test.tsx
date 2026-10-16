// @vitest-environment jsdom

// The sample review does not poll and the hub resync refreshes only the dataset list, so a dataset mutation is the
// only thing that tells an open review its rows and total moved. The old `["training", "datasets"]` prefix reached every
// dataset's sample pages; the generated keys have to reach them explicitly.

import { QueryClient } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it } from "vitest";

import {
	listToolMocksQueryKey,
	listTrainingDatasetsQueryKey,
	listTrainingDefinitionsQueryKey,
	listTrainingSamplesQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { useCancelTrainingDataset } from "@/features/training/queries/useTrainingDatasets";
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
