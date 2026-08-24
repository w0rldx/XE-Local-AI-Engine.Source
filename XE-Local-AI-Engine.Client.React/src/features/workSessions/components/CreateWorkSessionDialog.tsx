import { Alert, Button, Group, SegmentedControl, Stack, Text, TextInput, Textarea } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { AgentSelectorCard } from "@/features/chat/components/AgentSelectorCard";
import type { AgentOption } from "@/features/chat/models/ChatModels";
import { type WorkSessionKind, workSessionKinds } from "@/features/workSessions/models/WorkSessionModels";

export interface CreateWorkSessionDialogProps {
	readonly opened: boolean;
	readonly agentOptions: readonly AgentOption[];
	readonly isSubmitting: boolean;
	readonly errorMessage?: string;
	readonly onClose: () => void;
	readonly onSubmit: (values: { title: string; objective: string; kind: WorkSessionKind; agentDefinitionId: string }) => void;
}

const TITLE_MAX = 200;
const OBJECTIVE_MAX = 8000;

export function CreateWorkSessionDialog({
	opened,
	agentOptions,
	isSubmitting,
	errorMessage,
	onClose,
	onSubmit,
}: CreateWorkSessionDialogProps) {
	const { t } = useTranslation();
	const [title, setTitle] = useState("");
	const [objective, setObjective] = useState("");
	const [kind, setKind] = useState<WorkSessionKind>("General");
	const [agentDefinitionId, setAgentDefinitionId] = useState("");

	const trimmedTitle = title.trim();
	const trimmedObjective = objective.trim();
	const canSubmit = trimmedTitle.length > 0 && trimmedObjective.length > 0 && agentDefinitionId.length > 0 && !isSubmitting;

	const close = (): void => {
		setTitle("");
		setObjective("");
		setKind("General");
		setAgentDefinitionId("");
		onClose();
	};

	return (
		<DialogShell
			opened={opened}
			onClose={close}
			title={t("pages.workSessions.create.title", "New work session")}
			data-testid="create-work-session-dialog"
			// Anything typed here is unsaved until the create succeeds, so a stray overlay click must not discard it.
			confirmCloseWhen={trimmedTitle.length > 0 || trimmedObjective.length > 0}
			footer={
				<Group justify="flex-end">
					<Button variant="subtle" onClick={close} data-testid="create-work-session-cancel">
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						onClick={() => onSubmit({ title: trimmedTitle, objective: trimmedObjective, kind, agentDefinitionId })}
						disabled={!canSubmit}
						loading={isSubmitting}
						data-testid="create-work-session-submit"
					>
						{t("pages.workSessions.create.submit", "Create")}
					</Button>
				</Group>
			}
		>
			<Stack gap="md">
				{errorMessage ? (
					<Alert color="red" variant="light" data-testid="create-work-session-error">
						{errorMessage}
					</Alert>
				) : null}
				<TextInput
					label={t("pages.workSessions.create.titleLabel", "Title")}
					value={title}
					maxLength={TITLE_MAX}
					required={true}
					onChange={(event) => setTitle(event.currentTarget.value)}
					data-testid="create-work-session-title"
				/>
				<Textarea
					label={t("pages.workSessions.create.objectiveLabel", "Objective")}
					description={t("pages.workSessions.create.objectiveHint", "What should the agent achieve? It plans its own tasks from this.")}
					value={objective}
					maxLength={OBJECTIVE_MAX}
					required={true}
					autosize={true}
					minRows={4}
					maxRows={10}
					onChange={(event) => setObjective(event.currentTarget.value)}
					data-testid="create-work-session-objective"
				/>
				<Stack gap={4}>
					<Text size="sm" fw={500}>
						{t("pages.workSessions.create.kindLabel", "Kind")}
					</Text>
					<SegmentedControl
						value={kind}
						onChange={(value) => setKind(value as WorkSessionKind)}
						data={workSessionKinds.map((value) => ({ value, label: t(`pages.workSessions.kind.${value}`, value) }))}
						aria-label={t("pages.workSessions.create.kindLabel", "Kind")}
						data-testid="create-work-session-kind"
					/>
				</Stack>
				<Stack gap={4}>
					<Text size="sm" fw={500}>
						{t("pages.workSessions.create.agentLabel", "Agent")}
					</Text>
					{/* Reused unchanged from chat: it is purely presentational. "" is its Default-Assistant row, which is
					    not a valid session agent — the submit stays disabled until a real one is picked. */}
					<AgentSelectorCard
						agentOptions={agentOptions}
						agentModeEnabled={agentDefinitionId.length > 0}
						selectedAgentId={agentDefinitionId}
						onSelectAgent={setAgentDefinitionId}
					/>
				</Stack>
			</Stack>
		</DialogShell>
	);
}
