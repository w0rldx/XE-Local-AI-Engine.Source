import { createFileRoute } from "@tanstack/react-router";

import { NodeBinding } from "@/features/binding/pages/NodeBinding";

export const Route = createFileRoute("/_layout/node-binding")({
	component: NodeBinding,
});
