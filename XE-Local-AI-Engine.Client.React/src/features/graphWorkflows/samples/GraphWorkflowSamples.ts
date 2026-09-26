// The sample workflows the "New workflow" dialog offers under "Start from". Each is a plain wire graph posted through
// the ordinary create call, so a created sample is an ordinary definition. Every sample runs on any installed GGUF chat
// model and nothing else: no Tool, Agent or Chat-only nodes, and no model pin. Prompts inside the graphs are
// model-facing node config and stay English; only the name and description shown in the picker are translated.

import type { GraphWorkflowGraph } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import briefWithReviewLoop from "@/features/graphWorkflows/samples/brief-with-review-loop.json";
import parallelPerspectives from "@/features/graphWorkflows/samples/parallel-perspectives.json";
import summarizeWithApproval from "@/features/graphWorkflows/samples/summarize-with-approval.json";
import triageAndRoute from "@/features/graphWorkflows/samples/triage-and-route.json";

export interface GraphWorkflowSample {
	readonly id: string;
	readonly graph: GraphWorkflowGraph;
	readonly nameKey: string;
	readonly defaultName: string;
	readonly descriptionKey: string;
	readonly defaultDescription: string;
}

// The casts are unchecked here; GraphWorkflowSamples.test.ts and the backend GraphWorkflowSampleContractTests are the guarantee.
export const GRAPH_WORKFLOW_SAMPLES: readonly GraphWorkflowSample[] = [
	{
		id: "summarize-with-approval",
		graph: summarizeWithApproval as GraphWorkflowGraph,
		nameKey: "pages.graphWorkflows.samples.summarizeWithApproval.name",
		defaultName: "Summarize with approval",
		descriptionKey: "pages.graphWorkflows.samples.summarizeWithApproval.description",
		defaultDescription: "Summarizes a document, then waits for a person to approve or reject the summary.",
	},
	{
		id: "triage-and-route",
		graph: triageAndRoute as GraphWorkflowGraph,
		nameKey: "pages.graphWorkflows.samples.triageAndRoute.name",
		defaultName: "Triage and route",
		descriptionKey: "pages.graphWorkflows.samples.triageAndRoute.description",
		defaultDescription: "Classifies a customer message as a bug, a feature request or a question, and writes the matching reply.",
	},
	{
		id: "parallel-perspectives",
		graph: parallelPerspectives as GraphWorkflowGraph,
		nameKey: "pages.graphWorkflows.samples.parallelPerspectives.name",
		defaultName: "Parallel perspectives",
		descriptionKey: "pages.graphWorkflows.samples.parallelPerspectives.description",
		defaultDescription: "Reviews a proposal from three angles at once and merges them into one decision memo.",
	},
	{
		id: "brief-with-review-loop",
		graph: briefWithReviewLoop as GraphWorkflowGraph,
		nameKey: "pages.graphWorkflows.samples.briefWithReviewLoop.name",
		defaultName: "Brief with review loop",
		descriptionKey: "pages.graphWorkflows.samples.briefWithReviewLoop.description",
		defaultDescription:
			"Drafts a brief, has it checked for accuracy and clarity in parallel, revises it, and asks a person only when the model is unsure.",
	},
];
