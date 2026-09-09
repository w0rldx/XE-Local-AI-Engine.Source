import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import type { BenchmarkTaskItem, BenchmarkTaskItemDraft } from "@/features/benchmarks/models/BenchmarkTaskItems";
import { benchmarkTaskItemChildren } from "@/features/benchmarks/models/BenchmarkTaskItems";
import type { BenchmarkVerifierConfig } from "@/features/benchmarks/models/BenchmarkVerifier";

/** A numeric axis typed as text: `8192, 32768`. Kept as a string while editing so a half-typed number is not eaten. */
export const parseAxis = (value: string): number[] =>
	value
		.split(/[,\s]+/)
		.map((entry) => Number(entry))
		.filter((entry) => Number.isFinite(entry) && entry > 0);

export const formatAxis = (values: readonly number[]): string => values.join(", ");

export function otherLeafCount(items: readonly BenchmarkTaskItem[], leafCount: number, item: BenchmarkTaskItem | null): number {
	return item === null ? leafCount : leafCount - (item.kind === "niah" ? benchmarkTaskItemChildren(items, item.id).length : 1);
}

export const mutationFailure = (fallback: string) => (error: unknown) => toast.error(apiErrorMessage(error, fallback));

export const verifierOverride = (draft: BenchmarkTaskItemDraft, criterionId: string): BenchmarkVerifierConfig | null =>
	draft.verifierConfig?.[criterionId] ?? null;
