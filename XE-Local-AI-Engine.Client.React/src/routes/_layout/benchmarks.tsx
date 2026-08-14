import { createFileRoute } from "@tanstack/react-router";

import { BenchmarksPage } from "@/features/benchmarks/pages/BenchmarksPage";

export const Route = createFileRoute("/_layout/benchmarks")({ component: BenchmarksPage });
