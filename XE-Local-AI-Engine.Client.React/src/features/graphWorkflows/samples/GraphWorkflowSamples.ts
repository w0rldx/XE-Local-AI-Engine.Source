// The sample workflows the "New workflow" dialog offers under "Start from". Each is a plain wire graph posted through
// the ordinary create call, so a created sample is an ordinary definition. Every sample runs on any installed GGUF chat
// model and nothing else: no Tool or Chat-only nodes and no model pin. An Agent node is allowed only without an
// agentDefinitionId, because an unbound Agent runs the built-in default persona with the local tools, which every fresh
// install has; a bound agent would need setup first. Prompts inside the graphs are model-facing node config and stay
// English; only the name and description shown in the picker are translated.
//
// The two Development samples (they replace the retired Dev Workflows templates) also need Development Mode and a
// project, named by the run input's `developmentProjectId`. Their `repository` Agent nodes ship unbound, and the create
// binds them to a read-only repository agent (`bindRepositoryAgent`), because only a saved agent's tool list can offer
// the file tools and its id differs per node.

import type { GraphWorkflowGraph } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import agentResearchAndCheck from "@/features/graphWorkflows/samples/agent-research-and-check.json";
import briefWithReviewLoop from "@/features/graphWorkflows/samples/brief-with-review-loop.json";
import devIntake from "@/features/graphWorkflows/samples/dev-intake.json";
import parallelPerspectives from "@/features/graphWorkflows/samples/parallel-perspectives.json";
import researchPlanApproval from "@/features/graphWorkflows/samples/research-plan-approval.json";
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
	{
		id: "agent-research-and-check",
		graph: agentResearchAndCheck as GraphWorkflowGraph,
		nameKey: "pages.graphWorkflows.samples.agentResearchAndCheck.name",
		defaultName: "Agent research with check",
		descriptionKey: "pages.graphWorkflows.samples.agentResearchAndCheck.description",
		defaultDescription:
			"An agent answers a question with its tools and lists its sources, a second model checks the answer, and a person approves it.",
	},
	{
		id: "dev-intake",
		graph: devIntake as GraphWorkflowGraph,
		nameKey: "pages.graphWorkflows.samples.devIntake.name",
		defaultName: "Development intake",
		descriptionKey: "pages.graphWorkflows.samples.devIntake.description",
		defaultDescription:
			"Classifies a development request, answers a question from the repository, or plans a change, implements it with Development Mode and applies it after two approvals. Needs Development Mode and a project.",
	},
	{
		id: "research-plan-approval",
		graph: researchPlanApproval as GraphWorkflowGraph,
		nameKey: "pages.graphWorkflows.samples.researchPlanApproval.name",
		defaultName: "Research, plan, approve",
		descriptionKey: "pages.graphWorkflows.samples.researchPlanApproval.description",
		defaultDescription:
			"Researches a request in a Development project's repository, writes a plan and asks a person to approve it. Implements nothing. Needs Development Mode and a project.",
	},
];

/** The file tools a `repository` Agent node's agent must list (the server's `RepositoryToolNames`). */
const REPOSITORY_TOOL_NAMES: readonly string[] = ["list_files", "read_file", "search_text"];

function isUnboundRepositoryNode(node: NonNullable<GraphWorkflowGraph["nodes"]>[number]): boolean {
	const config = node.config as Record<string, unknown> | null | undefined;
	return node.kind === "Agent" && config?.["repository"] === true && config["agentDefinitionId"] == null;
}

/** Whether creating this graph needs an agent bound first: it holds a `repository` Agent node that names none. */
export function needsRepositoryAgent(graph: GraphWorkflowGraph): boolean {
	return (graph.nodes ?? []).some(isUnboundRepositoryNode);
}

/**
 * Binds every unbound `repository` Agent node to the first agent that lists file tools and nothing else, which on a
 * fresh install is the seeded "Coder (read-only)". With no such agent the graph is returned as it is, and the create
 * call answers with the server's sentence naming what the node needs.
 */
export function bindRepositoryAgent(
	graph: GraphWorkflowGraph,
	agents: readonly { readonly id: string; readonly allowedToolNames: readonly string[] }[],
): GraphWorkflowGraph {
	const agent = agents.find(
		(entry) => entry.allowedToolNames.length > 0 && entry.allowedToolNames.every((name) => REPOSITORY_TOOL_NAMES.includes(name)),
	);
	if (agent === undefined) {
		return graph;
	}
	return {
		...graph,
		nodes: (graph.nodes ?? []).map((node) =>
			isUnboundRepositoryNode(node)
				? { ...node, config: { ...(node.config as Record<string, unknown>), agentDefinitionId: agent.id } }
				: node,
		),
	};
}
