import { createFileRoute } from "@tanstack/react-router";

import { DatasetsPage } from "@/features/training/pages/DatasetsPage";

export const Route = createFileRoute("/_layout/training/datasets")({ component: DatasetsPage });
