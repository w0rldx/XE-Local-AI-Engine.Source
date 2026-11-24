import { Accordion, Badge, Group, SegmentedControl, Stack, Text } from "@mantine/core";
import type { Ref } from "react";
import { useTranslation } from "react-i18next";

import { ChatMarkdown } from "@/core/chat/components/ChatMarkdown";
import {
	type ExplanationBasis,
	type ExplanationLevel,
	explanationKey,
	inferredBadgeStyles,
} from "@/features/modelLab/models/ModelLabExplanation";

interface ExplanationLevelSwitchProps {
	value: ExplanationLevel;
	onChange: (level: ExplanationLevel) => void;
}

export function ExplanationLevelSwitch({ value, onChange }: ExplanationLevelSwitchProps) {
	const { t } = useTranslation();
	return (
		<Group gap="xs" justify="space-between">
			<Text size="sm" fw={600}>
				{t("pages.modelLab.explain.ui.heading", "Explanation")}
			</Text>
			<SegmentedControl
				size="xs"
				aria-label={t("pages.modelLab.explain.ui.levelLabel", "Explanation level")}
				value={value}
				onChange={(level) => onChange(level as ExplanationLevel)}
				data={[
					{ value: "overview", label: t("pages.modelLab.explain.ui.level.overview", "Overview") },
					{ value: "detail", label: t("pages.modelLab.explain.ui.level.detail", "Detail") },
					{ value: "math", label: t("pages.modelLab.explain.ui.level.math", "Math") },
				]}
				data-testid="model-lab-explain-level"
			/>
		</Group>
	);
}

type NamedBasis = Exclude<ExplanationBasis, { inferredFrom: string }>;

const basisLooks: Record<NamedBasis, { color: string; variant: "light" | "outline" }> = {
	measuredLlamaCpp: { color: "blue", variant: "light" },
	measuredTrace: { color: "blue", variant: "light" },
	derived: { color: "cyan", variant: "light" },
	structural: { color: "green", variant: "light" },
	simulated: { color: "grape", variant: "light" },
	interpreted: { color: "gray", variant: "light" },
	unavailable: { color: "gray", variant: "outline" },
};

interface BasisBadgeProps {
	basis: ExplanationBasis;
	"data-testid"?: string;
}

// The one evidence badge of Model Lab: every value or text states what it rests on with this.
export function BasisBadge({ basis, "data-testid": testId = "model-lab-explain-basis" }: BasisBadgeProps) {
	const { t } = useTranslation();
	if (typeof basis === "object") {
		return (
			<Badge variant="light" color="orange" maw="100%" styles={inferredBadgeStyles} data-testid={testId}>
				{t("pages.modelLab.architecture.inferredBadge", "Inferred from llama.cpp ({{source}})", {
					source: basis.inferredFrom,
				})}
			</Badge>
		);
	}
	const labels: Record<NamedBasis, string> = {
		measuredLlamaCpp: t("pages.modelLab.explain.ui.basisMeasuredLlamaCpp", "Measured by llama.cpp"),
		measuredTrace: t("pages.modelLab.explain.ui.basisMeasuredTrace", "Measured in the trace"),
		derived: t("pages.modelLab.explain.ui.basisDerived", "Derived"),
		structural: t("pages.modelLab.explain.ui.basisStructural", "Read from the file"),
		simulated: t("pages.modelLab.explain.ui.basisSimulated", "Simulated on this page"),
		interpreted: t("pages.modelLab.explain.ui.basisInterpreted", "Explanation, not measured"),
		unavailable: t("pages.modelLab.explain.ui.basisUnavailable", "Not available"),
	};
	return (
		<Badge variant={basisLooks[basis].variant} color={basisLooks[basis].color} data-testid={testId}>
			{labels[basis]}
		</Badge>
	);
}

interface ExplanationEntryProps {
	// Entry paths under root, most specific first.
	candidates: readonly string[];
	level: ExplanationLevel;
	basis: ExplanationBasis;
	title?: string;
	// The key path the candidates sit under; pages.modelLab.explain when unset.
	root?: string;
}

// One explanation at the chosen level with its basis, or nothing at all when no language has text for it.
export function ExplanationEntry({ candidates, level, basis, title, root }: ExplanationEntryProps) {
	const { t, i18n } = useTranslation();
	const key = explanationKey(i18n, candidates, level, root);
	if (key === null) {
		return null;
	}
	return (
		<Stack gap={4} data-testid="model-lab-explain-entry">
			<Group gap="xs">
				{title !== undefined && (
					<Text size="sm" fw={600}>
						{title}
					</Text>
				)}
				<BasisBadge basis={basis} />
			</Group>
			<ChatMarkdown content={t(key)} />
		</Stack>
	);
}

interface ExplanationListItem {
	id: string;
	label: string;
	candidates: readonly string[];
}

interface ExplanationListProps {
	items: readonly ExplanationListItem[];
	level: ExplanationLevel;
	basis: ExplanationBasis;
	root?: string;
	open: string | null;
	onOpen: (id: string | null) => void;
	"data-testid": string;
	ref?: Ref<HTMLDivElement>;
}

// A keyboard path through a set of explanations: each item is a button, its panel the explanation at the chosen level.
export function ExplanationList({ items, level, basis, root, open, onOpen, "data-testid": testId, ref }: ExplanationListProps) {
	return (
		<Accordion ref={ref} value={open} onChange={onOpen} variant="contained" chevronPosition="left" data-testid={testId}>
			{items.map((item) => (
				<Accordion.Item key={item.id} value={item.id} data-item-id={item.id}>
					<Accordion.Control>
						<Text size="sm">{item.label}</Text>
					</Accordion.Control>
					<Accordion.Panel>
						{/* Only the open item renders its Markdown; a closed panel stays mounted and would parse it for nothing. */}
						{open === item.id && <ExplanationEntry candidates={item.candidates} level={level} basis={basis} root={root} />}
					</Accordion.Panel>
				</Accordion.Item>
			))}
		</Accordion>
	);
}
