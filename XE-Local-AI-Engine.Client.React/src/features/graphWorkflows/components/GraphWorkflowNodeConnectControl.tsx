// "Connect to": the keyboard path to a new edge. A handle is a pointer target only, so this hands the editor the same
// connection a drag would, and the editor's `onConnect` rules (the refused second unconditional edge over one pair,
// the Pause context edges) apply unchanged.

import { Button, Group, Select } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { graphNodeSelectOption } from "@/features/graphWorkflows/components/config/GraphWorkflowSelectOptions";
import type { GraphWorkflowCanvasNodeData } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";

export interface GraphWorkflowNodeConnectControlProps {
	readonly node: GraphWorkflowCanvasNodeData;
	readonly graphNodes: readonly GraphWorkflowCanvasNodeData[];
	readonly onConnect: (target: string, sourceHandle: string | undefined) => void;
	readonly readOnly: boolean;
}

/** Keyed by the node's key where it is rendered, so a choice made for one node never carries over to the next. */
export function GraphWorkflowNodeConnectControl({ node, graphNodes, onConnect, readOnly }: GraphWorkflowNodeConnectControlProps) {
	const { t } = useTranslation();
	const [branch, setBranch] = useState<string | null>(null);
	const [target, setTarget] = useState<string | null>(null);

	// The source handles the card draws: a Condition's two branches, a Pause's offered decisions, one plain handle for
	// every other kind, and none on an End.
	const branches = node.kind === "Condition" ? ["true", "false"] : node.kind === "Pause" ? node.allowedDecisions : undefined;
	if (node.kind === "End" || branches?.length === 0) {
		return null;
	}
	// A Start node has no target handle, so it is no target here either.
	const targets = graphNodes.filter((other) => other.key !== node.key && other.kind !== "Start");
	const chosenTarget = targets.some((other) => other.key === target) ? target : null;
	const chosenBranch = branches?.find((entry) => entry === branch);
	const ready = chosenTarget !== null && (branches === undefined || chosenBranch !== undefined);

	return (
		<Group align="flex-end" gap="xs" wrap="wrap" data-testid="gw-node-connect">
			{branches === undefined ? null : (
				<Select
					label={t("pages.graphWorkflows.config.connectBranch", "From branch")}
					data={branches.map((entry) => ({
						value: entry,
						label: node.kind === "Pause" ? t(`pages.graphWorkflows.decision.${entry}`, entry) : entry,
					}))}
					value={chosenBranch ?? null}
					disabled={readOnly}
					onChange={setBranch}
					data-testid="gw-node-connect-branch"
				/>
			)}
			<Select
				label={t("pages.graphWorkflows.config.connectTo", "Connect to")}
				data={targets.map(graphNodeSelectOption)}
				value={chosenTarget}
				searchable={true}
				disabled={readOnly}
				onChange={setTarget}
				data-testid="gw-node-connect-target"
			/>
			<Button
				size="sm"
				variant="light"
				disabled={readOnly || !ready}
				onClick={() => {
					if (chosenTarget !== null) {
						onConnect(chosenTarget, chosenBranch);
						setTarget(null);
					}
				}}
				data-testid="gw-node-connect-submit"
			>
				{t("pages.graphWorkflows.config.connectSubmit", "Connect")}
			</Button>
		</Group>
	);
}
