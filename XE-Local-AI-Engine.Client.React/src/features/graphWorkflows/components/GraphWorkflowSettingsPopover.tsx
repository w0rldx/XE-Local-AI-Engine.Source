// The graph-level settings: the workflow `kind` and, for a Chat graph, its `chat` block. They are members of the graph
// DOCUMENT (they travel with Save, not with Rename), so every change here lands in the editor state and dirties the
// canvas like any node edit — which is why this is a popover beside Save and not a field in the name/description dialog.

import { Button, Popover, SegmentedControl, Stack, Switch, Text } from "@mantine/core";
import { IconSettings } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import {
	type GraphWorkflowGraphSettings,
	graphWorkflowChatDefaults,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import {
	graphWorkflowDefinitionKinds,
	narrowGraphWorkflowDefinitionKind,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

export interface GraphWorkflowSettingsPopoverProps {
	readonly settings: GraphWorkflowGraphSettings;
	readonly onChange: (next: GraphWorkflowGraphSettings) => void;
	readonly disabled?: boolean;
}

export function GraphWorkflowSettingsPopover({ settings, onChange, disabled = false }: GraphWorkflowSettingsPopoverProps) {
	const { t } = useTranslation();
	const chat = settings.chat ?? {};
	return (
		<Popover position="bottom-end" withArrow={true} shadow="md" trapFocus={true}>
			<Popover.Target>
				<Button
					size="xs"
					variant="default"
					leftSection={<IconSettings size={14} />}
					disabled={disabled}
					data-testid="gw-page-settings"
				>
					{t("pages.graphWorkflows.settings.open", "Workflow settings")}
				</Button>
			</Popover.Target>
			<Popover.Dropdown data-testid="gw-settings-dropdown">
				<Stack gap="sm" maw={320}>
					<Stack gap={4}>
						<Text size="sm" fw={500} id="gw-settings-kind-label">
							{t("pages.graphWorkflows.settings.kind", "Workflow kind")}
						</Text>
						<SegmentedControl
							aria-labelledby="gw-settings-kind-label"
							value={settings.kind}
							data={graphWorkflowDefinitionKinds.map((kind) => ({
								value: kind,
								label: t(`pages.graphWorkflows.settings.kindOption.${kind}`, kind),
							}))}
							onChange={(value) => onChange({ ...settings, kind: narrowGraphWorkflowDefinitionKind(value) })}
							data-testid="gw-settings-kind"
						/>
						<Text size="xs" c="dimmed">
							{t(
								"pages.graphWorkflows.settings.kindHelp",
								"A Chat workflow runs inside a conversation and may ask the user questions.",
							)}
						</Text>
					</Stack>
					{settings.kind === "Chat" ? (
						<>
							<Switch
								label={t("pages.graphWorkflows.settings.acceptsAttachments", "Accepts attachments")}
								checked={chat.acceptsAttachments ?? graphWorkflowChatDefaults.acceptsAttachments}
								onChange={(event) =>
									onChange({ ...settings, chat: { ...chat, acceptsAttachments: event.currentTarget.checked } })
								}
								data-testid="gw-settings-accepts-attachments"
							/>
							<Switch
								label={t("pages.graphWorkflows.settings.requireRerunConfirmation", "Confirm before running again")}
								checked={chat.requireRerunConfirmation ?? graphWorkflowChatDefaults.requireRerunConfirmation}
								onChange={(event) =>
									onChange({ ...settings, chat: { ...chat, requireRerunConfirmation: event.currentTarget.checked } })
								}
								data-testid="gw-settings-require-rerun-confirmation"
							/>
						</>
					) : null}
				</Stack>
			</Popover.Dropdown>
		</Popover>
	);
}
