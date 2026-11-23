import type { TFunction } from "i18next";

import type { XeLocalAiEngineClientEndpointsModelLabV1ModelLabRoutingResponse as RoutingResponse } from "@/core/api/generated/types.gen";
import type { ExpertUsage } from "@/features/modelLab/models/ModelLabRouting";

// Where a run's routing was measured, worded once for every view that shows it.
export function routingProvenanceNote(t: TFunction): string {
	return t(
		"pages.modelLab.trace.stepper.routing.note",
		"Read from each block's router as the model ran in PyTorch on the Hugging Face checkpoint. The GGUF in llama.cpp may route differently.",
	);
}

// What expert usage counts over: a cut-off routing file counts only its saved steps, so the title says so.
export function routingUsageTitle(t: TFunction, routing: Pick<RoutingResponse, "truncated" | "steps">): string {
	return routing.truncated
		? t("pages.modelLab.trace.stepper.routing.usageTruncated", "Most picked in the {{count}} saved steps", {
				count: routing.steps.length,
			})
		: t("pages.modelLab.trace.stepper.routing.usage", "Most picked in this run ({{count}} steps)", {
				count: routing.steps.length,
			});
}

// One expert's picks out of the same steps the title counts.
export function routingUsageCount(t: TFunction, usage: ExpertUsage, routing: Pick<RoutingResponse, "steps">): string {
	return t("pages.modelLab.trace.stepper.routing.usageCount", "E{{expert}}: picked in {{picks}} of {{count}} steps", {
		expert: usage.expert,
		picks: usage.picks,
		count: routing.steps.length,
	});
}
