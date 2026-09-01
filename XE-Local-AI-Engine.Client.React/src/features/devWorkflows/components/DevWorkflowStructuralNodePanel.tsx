import { Badge, Code, Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { DevWorkflowNodeStatusBadge } from "@/features/devWorkflows/components/DevWorkflowStatusBadge";
import {
	type DevWorkflowNodeRunDetailResponse,
	type DevWorkflowNodeRunSummaryResponse,
	type DevWorkflowNodeStatus,
	type DevWorkflowNodeType,
	type DevWorkflowRunResponse,
	toDevWorkflowNodeStatus,
} from "@/features/devWorkflows/models/DevWorkflowModels";

export interface DevWorkflowStructuralNodePanelProps {
	readonly nodeRun: DevWorkflowNodeRunDetailResponse;
	readonly nodeType: DevWorkflowNodeType;
	/** Siblings and the pinned graph. A structural node is described entirely by what surrounds it. */
	readonly run?: DevWorkflowRunResponse;
}

/**
 * ONE panel for Gate, Parallel and Join (P4 §2.6 — no bespoke component per kind), and for a SETTLED HumanGate, which
 * is a gate whose condition a person answered. All of them are nodes with no work of their own: what an operator needs
 * from them is the shape of the graph around them, which lives in the run's OTHER rows and the pinned graph's edges,
 * never on the node-run detail response.
 *
 * - **Gate / settled HumanGate** — the condition off the EDGES (Y24: conditions live on edges only, and P3 exposes no
 *   `conditionExpression` on the node), and which successor the run actually entered. There is no `conditionResult`
 *   field to read (P3 §4.2 deleted it); the branch NOT taken is the one the runtime left in an untaken state, which
 *   `untakenBranchStatuses` names.
 * - **Join** — dependencies satisfied against dependencies outstanding, which is the panel form of the
 *   `waitingOnNodeKeys` the node table already renders as a sentence.
 * - **Parallel** — the branches, each with its own status.
 *
 * Every list is drawn from the node KEY space, because that is what edges name; the status beside each entry comes
 * from joining back to the node-run rows. A successor the run has no row for is still listed — it is a template whose
 * children have not been materialized yet, and hiding it would make the graph look smaller than it is.
 */
export function DevWorkflowStructuralNodePanel({ nodeRun, nodeType, run }: DevWorkflowStructuralNodePanelProps) {
	const { t } = useTranslation();
	const nodeKey = nodeRun.nodeKey ?? "";
	const edges = run?.graph?.edges ?? [];
	const rowByKey = new Map<string, DevWorkflowNodeRunSummaryResponse>(
		(run?.nodes ?? []).map((node) => [node.nodeKey ?? "", node]),
	);
	const summary = rowByKey.get(nodeKey);
	const outbound = edges.filter((edge) => (edge.from ?? "") === nodeKey);
	const inbound = edges.filter((edge) => (edge.to ?? "") === nodeKey);
	// `waitingOnNodeKeys` is on the SUMMARY row, not the detail response — the run payload is the only place it exists.
	const waitingOn = new Set(summary?.waitingOnNodeKeys ?? []);

	return (
		<SectionCard
			title={t(`pages.devWorkflows.nodeType.${nodeType}`, nodeType)}
			gap="xs"
			data-testid="dev-workflow-node-structural"
		>
			{nodeType === "Join" ? (
				<Stack gap={4} data-testid="dev-workflow-node-structural-dependencies">
					<Text size="xs" c="dimmed">
						{t("pages.devWorkflows.structural.dependencies", "Dependencies")}
					</Text>
					{inbound.length === 0 ? (
						<Text size="sm" c="dimmed">
							{t("pages.devWorkflows.structural.noDependencies", "This node has no upstream dependencies in the pinned graph.")}
						</Text>
					) : (
						inbound.map((edge) => (
							<DependencyRow
								key={`${edge.from}>${edge.to}`}
								nodeKey={edge.from ?? ""}
								row={rowByKey.get(edge.from ?? "")}
								outstanding={waitingOn.has(edge.from ?? "")}
							/>
						))
					)}
				</Stack>
			) : (
				<Stack gap={4} data-testid="dev-workflow-node-structural-branches">
					<Text size="xs" c="dimmed">
						{nodeType === "Parallel"
							? t("pages.devWorkflows.structural.parallelBranches", "Parallel branches")
							: t("pages.devWorkflows.structural.branches", "Branches")}
					</Text>
					{outbound.length === 0 ? (
						<Text size="sm" c="dimmed">
							{t("pages.devWorkflows.structural.noBranches", "This node has no outgoing branches in the pinned graph.")}
						</Text>
					) : (
						outbound.map((edge) => {
							const target = rowByKey.get(edge.to ?? "");
							const condition = edge.condition;
							return (
								<Stack key={`${edge.from}>${edge.to}`} gap={2} data-testid={`dev-workflow-node-branch-${edge.to}`}>
									<BranchRow nodeKey={edge.to ?? ""} row={target} />
									{condition ? (
										// The declarative condition, as it is stored. Nothing here interprets it: an expression the
										// client paraphrased and the runtime read differently would be worse than no paraphrase.
										<Code data-testid={`dev-workflow-node-branch-condition-${edge.to}`}>
											{[condition.path, condition.op, JSON.stringify(condition.value ?? null)].filter(Boolean).join(" ")}
										</Code>
									) : (
										<Text size="xs" c="dimmed">
											{t("pages.devWorkflows.structural.unconditional", "always taken")}
										</Text>
									)}
								</Stack>
							);
						})
					)}
				</Stack>
			)}
		</SectionCard>
	);
}

/**
 * A dependency and whether it is still outstanding. "Outstanding" is the runtime's own answer (`waitingOnNodeKeys`),
 * not one derived from the upstream status here: a Skipped dependency can satisfy a join under one join policy and
 * not under another, and this panel does not own that rule.
 */
function DependencyRow({
	nodeKey,
	row,
	outstanding,
}: {
	readonly nodeKey: string;
	readonly row?: DevWorkflowNodeRunSummaryResponse;
	readonly outstanding: boolean;
}) {
	const { t } = useTranslation();
	return (
		<Group gap="xs" wrap="nowrap" data-testid={`dev-workflow-node-dependency-${nodeKey}`}>
			<Text size="sm" style={{ flex: 1, minWidth: 0 }} lineClamp={1}>
				{row?.label ?? nodeKey}
			</Text>
			{row?.status ? <DevWorkflowNodeStatusBadge status={toDevWorkflowNodeStatus(row.status)} /> : null}
			<Badge size="xs" variant="light" color={outstanding ? "orange" : "teal"}>
				{outstanding
					? t("pages.devWorkflows.structural.outstanding", "outstanding")
					: t("pages.devWorkflows.structural.satisfied", "satisfied")}
			</Badge>
		</Group>
	);
}

/**
 * The states a branch is in when the gate did NOT send the run down it.
 *
 * `Pending` is the branch that has not been judged yet. `Skipped` is the branch that WAS judged and lost: the state
 * machine reads a dead edge as `Admission.Skip` and the dispatcher writes the row `Skipped`, so on any settled gate the
 * branch not taken is a Skipped row, not a Pending one. Treating "not Pending" as taken therefore badged BOTH branches
 * of every decided gate — a lie on the one surface an operator reads to find out which way a run went. `Cancelled`
 * joins them: a run cancelled before a branch ran is not a branch the gate chose.
 */
const untakenBranchStatuses: readonly DevWorkflowNodeStatus[] = ["Pending", "Skipped", "Cancelled"];

/** A successor and its state. A branch with no row has not been reached — or is a template awaiting materialization. */
function BranchRow({ nodeKey, row }: { readonly nodeKey: string; readonly row?: DevWorkflowNodeRunSummaryResponse }) {
	const { t } = useTranslation();
	const status = row?.status ? toDevWorkflowNodeStatus(row.status) : undefined;
	return (
		<Group gap="xs" wrap="nowrap">
			<Text size="sm" style={{ flex: 1, minWidth: 0 }} lineClamp={1}>
				{row?.label ?? nodeKey}
			</Text>
			{status ? <DevWorkflowNodeStatusBadge status={status} /> : null}
			{/* The taken branch is the one the run actually entered — there is no `conditionResult` on the wire, and there
			    never was a need for one. What the branch NOT taken looks like is the runtime's answer, not a guess: see
			    `untakenBranchStatuses`. */}
			{status && !untakenBranchStatuses.includes(status) ? (
				<Badge size="xs" variant="light" color="blue" data-testid={`dev-workflow-node-branch-taken-${nodeKey}`}>
					{t("pages.devWorkflows.structural.taken", "taken")}
				</Badge>
			) : null}
			{row ? null : (
				<Badge size="xs" variant="light" color="gray">
					{t("pages.devWorkflows.structural.notMaterialized", "not created yet")}
				</Badge>
			)}
		</Group>
	);
}
