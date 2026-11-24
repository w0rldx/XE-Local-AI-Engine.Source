import { Group, Text } from "@mantine/core";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";

import { FieldInfo } from "@/core/ui/components/FieldInfo/FieldInfo";
import { BasisBadge } from "@/features/modelLab/components/ArchitectureExplanation";
import { formatTraceValue } from "@/features/modelLab/models/ModelLabArchitecture";

export type ArchitectureValueKey =
	| "fileType"
	| "declaredParamCount"
	| "blockCount"
	| "nextnPredictLayers"
	| "blocksInFile"
	| "mtpInFile"
	| "contextLength"
	| "embeddingLength"
	| "vocabulary"
	| "attentionHeadCount"
	| "attentionHeadCountKv"
	| "attentionKeyLength"
	| "attentionValueLength"
	| "fullAttentionInterval"
	| "expertCount"
	| "expertUsedCount";

export interface ArchitectureValue {
	key: ArchitectureValueKey;
	// Null or undefined: the file does not hold it, so it is not shown. Zero is shown.
	value: number | string | null | undefined;
	// Read from the file's header, or computed here from what the file holds.
	basis: "structural" | "derived";
}

function valueTexts(t: TFunction, mtpDeclared: boolean): Record<ArchitectureValueKey, { label: string; info: string }> {
	return {
		fileType: {
			label: t("pages.modelLab.architecture.values.fileType.label", "Quantization"),
			info: t(
				"pages.modelLab.architecture.values.fileType.info",
				"The file type the header declares (general.file_type): the quantization most weights were stored in. A single tensor can use another stored type; each tensor lists its own.",
			),
		},
		declaredParamCount: {
			label: t("pages.modelLab.architecture.values.declaredParamCount.label", "Parameters"),
			info: t(
				"pages.modelLab.architecture.values.declaredParamCount.info",
				"The parameter count the header declares (general.parameter_count). Not shown when the header has none; it is never summed from the tensors here.",
			),
		},
		blockCount: {
			label: t("pages.modelLab.architecture.values.blockCount.label", "Layers"),
			info: mtpDeclared
				? t(
						"pages.modelLab.architecture.values.blockCount.infoMtp",
						"The layers the header declares for the whole model (block_count), also those in another part of a split file. It includes the multi-token prediction layers (nextn_predict_layers), which are not part of the main stack.",
					)
				: t(
						"pages.modelLab.architecture.values.blockCount.info",
						"The layers the header declares for the whole model (block_count), also those in another part of a split file.",
					),
		},
		nextnPredictLayers: {
			label: t("pages.modelLab.architecture.values.nextnPredictLayers.label", "MTP layers"),
			info: t(
				"pages.modelLab.architecture.values.nextnPredictLayers.info",
				"The multi-token prediction layers the header declares (nextn_predict_layers). They predict further tokens on a path beside the main stack and are counted in the layers.",
			),
		},
		blocksInFile: {
			label: t("pages.modelLab.architecture.values.blocksInFile.label", "Blocks in this file"),
			info: t(
				"pages.modelLab.architecture.values.blocksInFile.info",
				"Counted from the tensors this file holds, without multi-token prediction blocks. One part of a split GGUF holds only some of the blocks.",
			),
		},
		mtpInFile: {
			label: t("pages.modelLab.architecture.values.mtpInFile.label", "MTP blocks in this file"),
			info: t(
				"pages.modelLab.architecture.values.mtpInFile.info",
				"Multi-token prediction blocks counted from the tensors this file holds.",
			),
		},
		contextLength: {
			label: t("pages.modelLab.architecture.values.contextLength.label", "Context length"),
			info: t(
				"pages.modelLab.architecture.values.contextLength.info",
				"The longest context in tokens the header declares (context_length). The context a server runs with is set separately and can be shorter.",
			),
		},
		embeddingLength: {
			label: t("pages.modelLab.architecture.values.embeddingLength.label", "Width"),
			info: t(
				"pages.modelLab.architecture.values.embeddingLength.info",
				"The length of each token's vector in the residual stream (embedding_length), the same in every block.",
			),
		},
		vocabulary: {
			label: t("pages.modelLab.architecture.values.vocabulary.label", "Vocabulary"),
			info: t(
				"pages.modelLab.architecture.values.vocabulary.info",
				"The number of tokens, from the shape of the token embedding (token_embd.weight, its second axis in GGUF order). Not shown when that tensor is missing or not two-dimensional.",
			),
		},
		attentionHeadCount: {
			label: t("pages.modelLab.architecture.values.attentionHeadCount.label", "Attention heads"),
			info: t(
				"pages.modelLab.architecture.values.attentionHeadCount.info",
				"The query heads of a full-attention block (attention.head_count).",
			),
		},
		attentionHeadCountKv: {
			label: t("pages.modelLab.architecture.values.attentionHeadCountKv.label", "Key/value heads"),
			info: t(
				"pages.modelLab.architecture.values.attentionHeadCountKv.info",
				"The key and value heads of a full-attention block (attention.head_count_kv). Fewer than the query heads means several query heads share one key/value head.",
			),
		},
		attentionKeyLength: {
			label: t("pages.modelLab.architecture.values.attentionKeyLength.label", "Key head size"),
			info: t(
				"pages.modelLab.architecture.values.attentionKeyLength.info",
				"The length of each key head (attention.key_length).",
			),
		},
		attentionValueLength: {
			label: t("pages.modelLab.architecture.values.attentionValueLength.label", "Value head size"),
			info: t(
				"pages.modelLab.architecture.values.attentionValueLength.info",
				"The length of each value head (attention.value_length).",
			),
		},
		fullAttentionInterval: {
			label: t("pages.modelLab.architecture.values.fullAttentionInterval.label", "Full-attention interval"),
			info: t(
				"pages.modelLab.architecture.values.fullAttentionInterval.info",
				"Every Nth block uses full attention and the others linear attention, N being this value (full_attention_interval).",
			),
		},
		expertCount: {
			label: t("pages.modelLab.architecture.values.expertCount.label", "Experts"),
			info: t("pages.modelLab.architecture.values.expertCount.info", "The experts of a mixture-of-experts block (expert_count)."),
		},
		expertUsedCount: {
			label: t("pages.modelLab.architecture.values.expertUsedCount.label", "Experts per token"),
			info: t(
				"pages.modelLab.architecture.values.expertUsedCount.info",
				"How many experts each token is routed to in a mixture-of-experts block (expert_used_count).",
			),
		},
	};
}

interface ArchitectureValuesProps {
	values: readonly ArchitectureValue[];
	// The header declares multi-token prediction layers, so the layers' text names them.
	mtpDeclared: boolean;
}

// Read-only values, each with what it rests on and an (i) that says what it is.
export function ArchitectureValues({ values, mtpDeclared }: ArchitectureValuesProps) {
	const { t, i18n } = useTranslation();
	const texts = valueTexts(t, mtpDeclared);
	const shown = values.filter((item) => item.value !== null && item.value !== undefined);
	if (shown.length === 0) {
		return null;
	}
	return (
		<Group gap="md" data-testid="model-lab-architecture-values">
			{shown.map((item) => (
				<Group key={item.key} gap={4} data-testid={`model-lab-architecture-value-${item.key}`}>
					<Text size="sm" c="dimmed">
						{texts[item.key].label}
					</Text>
					<FieldInfo
						label={texts[item.key].label}
						text={texts[item.key].info}
						testId={`model-lab-architecture-value-info-${item.key}`}
					/>
					<Text size="sm" fw={600} data-testid={`model-lab-architecture-value-text-${item.key}`}>
						{typeof item.value === "number" ? formatTraceValue(item.value, i18n.language) : item.value}
					</Text>
					<BasisBadge basis={item.basis} data-testid={`model-lab-architecture-value-basis-${item.key}`} />
				</Group>
			))}
		</Group>
	);
}
