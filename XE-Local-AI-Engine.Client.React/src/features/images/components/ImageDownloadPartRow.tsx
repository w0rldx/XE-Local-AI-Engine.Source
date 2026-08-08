import { ActionIcon, Card, Group, Select, Stack, TextInput, Tooltip } from "@mantine/core";
import { IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { type ImageModelPartRole, imageModelPartRoles, type PartDraft } from "@/features/images/models/ImageModels";

const partRoles = imageModelPartRoles;

export interface PartRowProps {
	part: PartDraft;
	index: number;
	canRemove: boolean;
	onChange: (id: string, patch: Partial<PartDraft>) => void;
	onRemove: (id: string) => void;
}

// One declared file of a multi-part set. Only the role and file name are required; the repository override, size and
// digest are the fields that make a real cross-repo install work and its progress honest, so they are visible rather
// than hidden behind another disclosure.
export function PartRow({ part, index, canRemove, onChange, onRemove }: PartRowProps) {
	const { t } = useTranslation();

	return (
		<Card withBorder={true} padding="sm" radius="sm" data-testid={`image-model-download-part-${index}`}>
			<Stack gap="xs">
				<Group align="flex-end" wrap="nowrap" gap="xs">
					<Select
						label={t("pages.images.models.download.advanced.role.label", "Role")}
						data={partRoles.map((role) => ({ value: role, label: t(`pages.images.models.partRoles.${role}`, role) }))}
						value={part.role}
						allowDeselect={false}
						w={150}
						onChange={(value) => onChange(part.id, { role: (value ?? part.role) as ImageModelPartRole })}
						data-testid={`image-model-download-part-role-${index}`}
					/>
					<TextInput
						label={t("pages.images.models.download.advanced.fileName.label", "File")}
						placeholder="Qwen_Image-Q4_K_M.gguf"
						value={part.fileName}
						style={{ flex: 1 }}
						onChange={(event) => onChange(part.id, { fileName: event.currentTarget.value })}
						data-testid={`image-model-download-part-file-${index}`}
					/>
					<Tooltip label={t("pages.images.models.download.advanced.removePart", "Remove file")}>
						<ActionIcon
							variant="light"
							color="red"
							aria-label={t("pages.images.models.download.advanced.removePart", "Remove file")}
							disabled={!canRemove}
							onClick={() => onRemove(part.id)}
							data-testid={`image-model-download-part-remove-${index}`}
						>
							<IconX size={16} />
						</ActionIcon>
					</Tooltip>
				</Group>
				<Group grow={true} align="flex-start">
					<TextInput
						label={t("pages.images.models.download.advanced.repoId.label", "Repository (optional)")}
						placeholder={t("pages.images.models.download.advanced.repoId.placeholder", "Same as above")}
						value={part.repoId}
						onChange={(event) => onChange(part.id, { repoId: event.currentTarget.value })}
						data-testid={`image-model-download-part-repo-${index}`}
					/>
					<TextInput
						label={t("pages.images.models.download.advanced.sizeBytes.label", "Size in bytes (optional)")}
						placeholder="13065746976"
						inputMode="numeric"
						value={part.sizeBytes}
						onChange={(event) => onChange(part.id, { sizeBytes: event.currentTarget.value })}
						data-testid={`image-model-download-part-size-${index}`}
					/>
					<TextInput
						label={t("pages.images.models.download.advanced.sha256.label", "SHA-256 (optional)")}
						value={part.sha256}
						onChange={(event) => onChange(part.id, { sha256: event.currentTarget.value })}
						data-testid={`image-model-download-part-sha-${index}`}
					/>
				</Group>
			</Stack>
		</Card>
	);
}
