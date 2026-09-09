import { ActionIcon, Group, NumberInput, Select, TextInput } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { maxSampleKindLength, type SampleKindDraft } from "@/features/training/models/DefinitionEditorModels";
import type { SampleLabel } from "@/features/training/models/TrainingModels";

interface SampleKindRowProps {
	kind: SampleKindDraft;
	/** The last remaining row cannot be removed — a definition needs at least one sample kind. */
	removeDisabled: boolean;
	onChange: (change: Partial<SampleKindDraft>) => void;
	onRemove: () => void;
}

export function SampleKindRow({ kind, removeDisabled, onChange, onRemove }: SampleKindRowProps) {
	const { t } = useTranslation();

	// DialogShell goes full-screen below 768px, which leaves this row under 360px wide — too narrow
	// for all four controls. Wrapping puts the kind on its own line there and keeps count, label
	// and the delete button together underneath, instead of overflowing the dialog body.
	return (
		<Group align="flex-end" gap="xs" data-testid="training-definition-kind-row">
			<TextInput
				aria-label={t("training.definitions.editor.kind", "Kind")}
				flex="1 1 200px"
				maxLength={maxSampleKindLength}
				miw={0}
				onChange={(event) => onChange({ kind: event.currentTarget.value })}
				placeholder={t("training.definitions.editor.kindPlaceholder", "e.g. single-tool-call")}
				value={kind.kind}
			/>
			<NumberInput
				aria-label={t("training.definitions.editor.count", "Count")}
				flex="0 0 100px"
				min={1}
				onChange={(value) => onChange({ count: typeof value === "number" ? value : 0 })}
				value={kind.count}
			/>
			<Select
				allowDeselect={false}
				aria-label={t("training.definitions.editor.label", "Label")}
				data={["Good", "Bad"]}
				flex="0 0 110px"
				onChange={(value) => onChange({ label: (value ?? "Good") as SampleLabel })}
				value={kind.label}
			/>
			<ActionIcon
				aria-label={t("training.definitions.editor.removeKind", "Remove kind")}
				color="red"
				disabled={removeDisabled}
				onClick={onRemove}
				variant="subtle"
			>
				<IconTrash size={16} />
			</ActionIcon>
		</Group>
	);
}
