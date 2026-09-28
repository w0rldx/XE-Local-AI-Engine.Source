import { Badge, type MantineSize, Tooltip, VisuallyHidden } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientServicesKnowledgeKnowledgeScoreKind as KnowledgeScoreKind } from "@/core/api/generated";

interface KnowledgeScoreBadgeProps {
	readonly score: number;
	readonly scoreKind: KnowledgeScoreKind;
	readonly size?: MantineSize;
}

// Names which kind of score a knowledge hit carries instead of showing a bare number: a fused RRF value (≈0.01–0.06)
// and a raw cross-encoder relevance (unbounded, e.g. −11…+7) share one field but no scale, and neither is a
// probability. The badge says the kind; the raw number sits in a kind-labelled tooltip for the operator who wants it.
// Fusion needs three decimals to tell hits apart, rerank two. Shared by the knowledge search panel and the chat
// sources strip, so it lives in core rather than in either feature.
export function KnowledgeScoreBadge({ score, scoreKind, size }: KnowledgeScoreBadgeProps) {
	const { t, i18n } = useTranslation();
	const digits = scoreKind === "Rerank" ? 2 : 3;
	const value = new Intl.NumberFormat(i18n.language, { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(
		score,
	);

	const detail =
		scoreKind === "Rerank"
			? t("pages.knowledgeBase.scoreKind.rerankTooltip", "Cross-encoder relevance {{value}}", { value })
			: t("pages.knowledgeBase.scoreKind.fusionTooltip", "Fusion score {{value}}", { value });

	// The Badge is not focusable (it may sit inside a button), so the hover tooltip alone would hide the number from
	// keyboard, screen-reader and touch users; the same text rides inside the badge, visually hidden.
	return (
		<Tooltip label={detail} withArrow={true}>
			<Badge size={size} variant="light" color="primary" style={{ flexShrink: 0 }} data-testid="knowledge-score-kind">
				{scoreKind === "Rerank"
					? t("pages.knowledgeBase.scoreKind.rerank", "Reranked")
					: t("pages.knowledgeBase.scoreKind.fusion", "Hybrid match")}
				<VisuallyHidden component="span" data-testid="knowledge-score-detail">
					{" "}
					{detail}
				</VisuallyHidden>
			</Badge>
		</Tooltip>
	);
}
