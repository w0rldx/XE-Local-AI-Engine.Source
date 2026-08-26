import { Button, Group, Select, Switch, TextInput } from "@mantine/core";
import { IconPlayerPlay } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

interface InferenceProfileExploreFormProps {
	readonly modelName: string;
	readonly role: string;
	readonly allowPreSpawnVramPressure: boolean;
	readonly isPending: boolean;
	readonly onModelNameChange: (value: string) => void;
	readonly onRoleChange: (value: string) => void;
	readonly onAllowPreSpawnVramPressureChange: (value: boolean) => void;
	readonly onExplore: () => void;
}

export function InferenceProfileExploreForm({
	modelName,
	role,
	allowPreSpawnVramPressure,
	isPending,
	onModelNameChange,
	onRoleChange,
	onAllowPreSpawnVramPressureChange,
	onExplore,
}: InferenceProfileExploreFormProps) {
	const { t } = useTranslation();
	const roleData = [
		{ value: "chat", label: t("pages.modelFit.inferenceProfiles.explore.roleChat", "Chat") },
		{ value: "embedding", label: t("pages.modelFit.inferenceProfiles.explore.roleEmbedding", "Embedding") },
		{ value: "reranker", label: t("pages.modelFit.inferenceProfiles.explore.roleReranker", "Reranker") },
	];

	return (
		<>
			<Group align="flex-end" gap="sm" wrap="wrap">
				<TextInput
					label={t("pages.modelFit.inferenceProfiles.explore.modelLabel", "Model name")}
					placeholder={t("pages.modelFit.inferenceProfiles.explore.modelPlaceholder", "e.g. unsloth/Qwen3-4B-GGUF")}
					value={modelName}
					onChange={(event) => onModelNameChange(event.currentTarget.value)}
					data-testid="inference-profile-explore-model"
					style={{ flex: 1, minWidth: 200, maxWidth: 320 }}
				/>
				<Select
					label={t("pages.modelFit.inferenceProfiles.explore.roleLabel", "Role")}
					data={roleData}
					value={role}
					onChange={(value) => onRoleChange(value ?? "chat")}
					allowDeselect={false}
					data-testid="inference-profile-explore-role"
					w={180}
				/>
				<Button
					leftSection={<IconPlayerPlay size={16} />}
					loading={isPending}
					disabled={modelName.trim().length === 0}
					onClick={onExplore}
					data-testid="inference-profile-explore-button"
				>
					{t("pages.modelFit.inferenceProfiles.explore.button", "Explore")}
				</Button>
			</Group>
			<Switch
				checked={allowPreSpawnVramPressure}
				onChange={(event) => onAllowPreSpawnVramPressureChange(event.currentTarget.checked)}
				color="orange"
				label={t(
					"pages.modelFit.inferenceProfiles.allowPreSpawnVramPressure.label",
					"Allow benchmarks despite existing VRAM pressure",
				)}
				description={t(
					"pages.modelFit.inferenceProfiles.allowPreSpawnVramPressure.description",
					"Operator override: bypasses only the pre-spawn ambient-pressure gate. New pressure caused during the benchmark still invalidates the run.",
				)}
				data-testid="inference-profile-allow-pre-spawn-vram-pressure"
			/>
		</>
	);
}
