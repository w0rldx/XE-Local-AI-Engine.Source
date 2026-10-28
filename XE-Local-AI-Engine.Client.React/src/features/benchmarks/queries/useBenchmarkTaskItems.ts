import { useMutation, useQuery } from "@tanstack/react-query";

import {
	createBenchmarkTaskItem,
	deleteBenchmarkTaskItem,
	listBenchmarkTaskItems,
	reorderBenchmarkTaskItems,
	updateBenchmarkTaskItem,
} from "@/core/api/generated";
import { listBenchmarkTaskItemsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import type { BenchmarkTaskItem, BenchmarkTaskItemDraft } from "@/features/benchmarks/models/BenchmarkTaskItems";
import { pruneVerifierOverrides, toBenchmarkTaskItem } from "@/features/benchmarks/models/BenchmarkTaskItems";
import { useBenchmarkInvalidation } from "@/features/benchmarks/queries/useBenchmarks";

// Task-item writes change the item-set hash; invalidate runs/cells and warn the operator before mutation.

/** One project's task items, in index order, with the set hash the node currently computes over them. */
export interface BenchmarkTaskItemList {
	items: BenchmarkTaskItem[];
	/** Null until the first item write. A run stamped with a different one is excluded as `item-set-revised`. */
	taskItemSetHash: string | null;
	/** The version an item CREATE must be made against — adding an item changes what a freeze would produce. */
	projectVersion: number;
}

export function useBenchmarkTaskItems(projectId: string | null) {
	return useQuery<BenchmarkTaskItemList>({
		queryKey: listBenchmarkTaskItemsQueryKey({ path: { projectId: projectId ?? "" } }),
		enabled: Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await listBenchmarkTaskItems({ path: { projectId: projectId as string }, signal });
			return {
				items: (data.items ?? []).map(toBenchmarkTaskItem).sort((left, right) => left.index - right.index),
				taskItemSetHash: data.taskItemSetHash ?? null,
				projectVersion: data.projectVersion ?? 0,
			};
		},
	});
}

// The generator members are omitted rather than sent as null when unset, exactly as the project body does it: an
// absent blob is "this item has none", and a present empty one is a blob the node has to parse and refuse.
const taskItemMutationBody = (draft: BenchmarkTaskItemDraft) => ({
	prompt: draft.prompt,
	kind: draft.kind,
	countsTowardScore: draft.countsTowardScore,
	...(draft.referenceAnswer === null ? {} : { referenceAnswer: draft.referenceAnswer }),
	...(pruneVerifierOverrides(draft.verifierConfig) === null
		? {}
		: { verifierConfig: pruneVerifierOverrides(draft.verifierConfig) }),
	...(draft.generatorConfig === null ? {} : { generatorConfig: draft.generatorConfig }),
});

export function useCreateBenchmarkTaskItem() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			expectedProjectVersion,
			draft,
		}: {
			projectId: string;
			expectedProjectVersion: number;
			draft: BenchmarkTaskItemDraft;
		}) => {
			const { data } = await createBenchmarkTaskItem({
				path: { projectId },
				body: { ...taskItemMutationBody(draft), expectedProjectVersion },
			});
			return toBenchmarkTaskItem(data);
		},
		onSuccess: () => invalidate(),
	});
}

export function useUpdateBenchmarkTaskItem() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			item,
			draft,
		}: {
			projectId: string;
			/** The ITEM's version, not the project's — an edit is a write to one item. */
			item: Pick<BenchmarkTaskItem, "id" | "version">;
			draft: BenchmarkTaskItemDraft;
		}) => {
			const { data } = await updateBenchmarkTaskItem({
				path: { projectId, itemId: item.id },
				body: { ...taskItemMutationBody(draft), expectedVersion: item.version },
			});
			return toBenchmarkTaskItem(data);
		},
		onSuccess: () => invalidate(),
	});
}

export function useDeleteBenchmarkTaskItem() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: ({ projectId, item }: { projectId: string; item: Pick<BenchmarkTaskItem, "id" | "version"> }) =>
			deleteBenchmarkTaskItem({
				path: { projectId, itemId: item.id },
				body: { expectedVersion: item.version },
			}),
		onSuccess: () => invalidate(),
	});
}

/**
 * The whole new order at once. Naming every current id IS the concurrency check — an item added or deleted while the
 * operator was dragging makes the two sets disagree and the node refuses — so there is no version token to send.
 */
export function useReorderBenchmarkTaskItems() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ projectId, itemIds }: { projectId: string; itemIds: readonly string[] }) => {
			const { data } = await reorderBenchmarkTaskItems({ path: { projectId }, body: { itemIds: [...itemIds] } });
			return (data.items ?? []).map(toBenchmarkTaskItem).sort((left, right) => left.index - right.index);
		},
		onSuccess: () => invalidate(),
	});
}
