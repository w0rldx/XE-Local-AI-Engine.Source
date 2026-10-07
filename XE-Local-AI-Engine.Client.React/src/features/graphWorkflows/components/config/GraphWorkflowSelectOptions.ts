// A stored value that is not in the live option list — an agent that was deleted, a model that is no longer installed,
// a tool the node stopped offering — renders as BLANK in a Mantine `Select`, which reads as "nothing is configured"
// when the graph in fact still carries it. Appending it as its own option keeps the configured value visible, and the
// save gate (client rules, then the server) is what decides whether it is still runnable.

import type { TFunction } from "i18next";

export interface GraphWorkflowSelectOption {
	readonly value: string;
	readonly label: string;
}

/** A model option; `cloudProvider` names where a cloud entry is served and is absent for a node-local model. */
export interface GraphWorkflowModelSelectOption extends GraphWorkflowSelectOption {
	readonly cloudProvider?: string | undefined;
}

/** Marks cloud entries in a model picker so an author can tell them from the node's own models. */
export function withCloudModelLabels(
	options: readonly GraphWorkflowModelSelectOption[],
	t: TFunction,
): GraphWorkflowSelectOption[] {
	return options.map((option) =>
		option.cloudProvider === undefined
			? option
			: {
					value: option.value,
					label: t("pages.graphWorkflows.config.cloudModelLabel", "{{label}} · {{provider}} (cloud)", {
						label: option.label,
						provider: option.cloudProvider,
					}),
				},
	);
}

export function withCurrentValue(
	options: readonly GraphWorkflowSelectOption[],
	value: string | null | undefined,
): GraphWorkflowSelectOption[] {
	const list = options.map((option) => ({ value: option.value, label: option.label }));
	return value && !list.some((option) => option.value === value) ? [...list, { value, label: value }] : list;
}
