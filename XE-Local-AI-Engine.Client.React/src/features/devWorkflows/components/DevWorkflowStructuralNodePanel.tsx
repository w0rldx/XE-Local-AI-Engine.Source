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
	isSettledDevWorkflowNodeStatus,
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
 * - **Join** — every inbound dependency with the verdict `Admission` will read off it: satisfied, still waiting, or
 *   DEAD. See `DependencyRow`; the verdict is the state machine's own edge rule, not a paraphrase of it.
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
	const outbound = edges.filter((edge) => (edge.from ?? "") === nodeKey);
	const inbound = edges.filter((edge) => (edge.to ?? "") === nodeKey);
	// A template key never gets a node run — its children get the rows — so it is not a dependency at all, and reading
	// its row-less-ness as anything but "template" claimed a node that had not run and could not run had satisfied one.
	// `isTemplate` is the SERVER's own `TemplateSubtree` verdict on the pinned graph (Slice D), which replaced a client
	// mirror of that walk: one walk, one answer, and no way for the two to drift apart on a graph shape neither side
	// had been tried against.
	const templates = new Set(
		(run?.graph?.nodes ?? []).filter((node) => node.isTemplate === true).map((node) => node.nodeKey ?? ""),
	);
	// The ONE thing a dead edge's wording turns on. `All` is the parser's own default for an absent policy
	// (`DevWorkflowGraph.cs:257`): under `All` a single dead edge is why the join SKIPS, under `Any` it is ignored for
	// as long as a sibling is satisfied.
	const joinSkipsOnDead =
		((run?.graph?.nodes ?? []).find((node) => (node.nodeKey ?? "") === nodeKey)?.joinPolicy ?? "All").toLowerCase() !==
		"any";

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
								joinSkipsOnDead={joinSkipsOnDead}
								isTemplate={templates.has(edge.from ?? "")}
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
 * A dependency and the verdict `Admission` will reach on its edge. This MIRRORS `DevWorkflowStateMachine.EdgeState`,
 * which is the function that actually decides: a source that has not settled leaves the edge Pending (WAITING); a
 * `Succeeded` source satisfies it; a `Failed` or `Cancelled` one kills it (DEAD), and under an `All` join one dead
 * edge is precisely why the join will SKIP rather than succeed.
 *
 * A `Skipped` source is the third answer (C1), and the ONE this row does not judge for itself. The state machine
 * waives a skip — the join carries on — only when nothing upstream of it was dead, which is a recursion over every
 * ancestor's row and the whole pinned graph. The ancestor that decides it need not be among the dependencies drawn
 * here: in `failed → skipped → join` beside a succeeded sibling the runtime SKIPS the join, and a row reading its own
 * status said the join would carry on. So the server sends the verdict as `skipWaived`, computed with the state
 * machine's own predicate, and this row renders it: `true` waived, `false` dead. An older server sends neither, and
 * then the badge says the row is skipped and claims nothing about the join.
 *
 * It is deliberately NOT `waitingOnNodeKeys` any more. The runtime sends that list only while the join itself is
 * Pending and drops every SETTLED source from it — so a Skipped branch arrived as "not waited on" and this row badged
 * it SATISFIED, telling an operator the opposite of what the join was about to do with it (LIVE-3 P2).
 *
 * The one edge state the panel cannot see is a `Succeeded` source whose edge CONDITION did not fire: judging that
 * needs the source's output document, and the summary row does not carry one. Join dependencies are unconditional in
 * every shape we ship. ponytail: a conditional join edge would need `outputJson` on the summary row to read honestly.
 *
 * A materialization TEMPLATE is none of these, and is named as what it is instead.
 */
function DependencyRow({
	nodeKey,
	row,
	joinSkipsOnDead,
	isTemplate,
}: {
	readonly nodeKey: string;
	readonly row?: DevWorkflowNodeRunSummaryResponse;
	/** `All` (the default) skips the join on a dead edge; `Any` carries on without it. Only the wording differs. */
	readonly joinSkipsOnDead: boolean;
	readonly isTemplate: boolean;
}) {
	const { t } = useTranslation();
	const status = row?.status ? toDevWorkflowNodeStatus(row.status) : undefined;
	const settled = status !== undefined && isSettledDevWorkflowNodeStatus(status);
	const isDead = settled && status !== "Succeeded";
	// Under `Any` a skip is simply the branch that did not carry the join, which is the DEAD wording already. It is
	// only `All` where the two skips part company, and there the answer is the SERVER's: `undefined` (an older server,
	// or a graph it could not route) is a skip we say nothing about rather than one we guess at.
	const skipUnderAll = isDead && status === "Skipped" && joinSkipsOnDead;
	const isWaived = skipUnderAll && row?.skipWaived === true;
	const isUnjudgedSkip = skipUnderAll && (row?.skipWaived === null || row?.skipWaived === undefined);
	return (
		<Group gap="xs" wrap="nowrap" data-testid={`dev-workflow-node-dependency-${nodeKey}`}>
			<Text size="sm" style={{ flex: 1, minWidth: 0 }} lineClamp={1}>
				{row?.label ?? nodeKey}
			</Text>
			{status ? <DevWorkflowNodeStatusBadge status={status} /> : null}
			{isTemplate && !row ? (
				<Badge size="xs" variant="light" color="gray">
					{t("pages.devWorkflows.structural.template", "template — materializes per task")}
				</Badge>
			) : isWaived ? (
				<Badge size="xs" variant="light" color="gray">
					{t("pages.devWorkflows.structural.waivedAll", "skipped — the join carries on if a sibling succeeded")}
				</Badge>
			) : isUnjudgedSkip ? (
				<Badge size="xs" variant="light" color="gray">
					{t("pages.devWorkflows.structural.skipped", "skipped")}
				</Badge>
			) : isDead ? (
				<Badge size="xs" variant="light" color="red">
					{joinSkipsOnDead
						? t("pages.devWorkflows.structural.deadAll", "dead — the join skips once nothing is pending")
						: t("pages.devWorkflows.structural.dead", "dead — this branch will not carry the join")}
				</Badge>
			) : (
				<Badge size="xs" variant="light" color={settled ? "teal" : "orange"}>
					{settled
						? t("pages.devWorkflows.structural.satisfied", "satisfied")
						: t("pages.devWorkflows.structural.outstanding", "outstanding")}
				</Badge>
			)}
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
