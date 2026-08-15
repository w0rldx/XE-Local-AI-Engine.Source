import { createFileRoute } from "@tanstack/react-router";
import { z } from "zod";

import { BenchmarksPage } from "@/features/benchmarks/pages/BenchmarksPage";

// A comparison report links here with the two model names it compared, so the page can open on those runs instead of
// the newest two. Both are optional: the page is reachable, and useful, with no search at all.
const benchmarksSearchSchema = z.object({
	base: z.string().optional(),
	tuned: z.string().optional(),
});

export const Route = createFileRoute("/_layout/benchmarks")({
	validateSearch: benchmarksSearchSchema,
	component: BenchmarksRoute,
});

// Thin router adapter: BenchmarksPage stays router-free (it is rendered directly in unit tests), matching the preview
// route's split.
function BenchmarksRoute() {
	const { base, tuned } = Route.useSearch();
	return <BenchmarksPage baseModelName={base} tunedModelName={tuned} />;
}
