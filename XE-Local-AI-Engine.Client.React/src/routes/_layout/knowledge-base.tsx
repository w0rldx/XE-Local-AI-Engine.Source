import { createFileRoute } from "@tanstack/react-router";

import { KnowledgeBase } from "@/features/knowledge/pages/KnowledgeBase";

export const Route = createFileRoute("/_layout/knowledge-base")({
	component: KnowledgeBase,
});
