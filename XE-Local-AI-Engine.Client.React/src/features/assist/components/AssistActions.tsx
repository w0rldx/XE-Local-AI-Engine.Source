import { Box, Button, Group, Tooltip } from "@mantine/core";
import { IconSparkles, IconWand } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { AssistDraft, AssistExistingContent, AssistMode, AssistSurface } from "@/features/assist/models/AssistModels";
import { GenerationAssistDialog } from "@/features/assist/components/GenerationAssistDialog";
import { isLocalChatModel } from "@/features/chat/pages/ChatModelOptions";
import { useLoadedModels } from "@/features/loaded-models/queries/useLoadedModels";

interface AssistActionsProps {
	surface: AssistSurface;
	/** Live form values. Drives the Improve baseline and whether there is anything to improve yet. */
	existing: AssistExistingContent;
	onApply: (draft: AssistDraft) => void;
	onDiscard: () => void;
}

/**
 * The "Draft with AI" / "Improve with AI" entry point both editors embed, plus the dialog it opens.
 *
 * Eligibility shown here is a UI HINT only — the server re-checks the model fail-closed on every draft. It exists so
 * a node with no installed chat model explains itself up front instead of failing on the first Generate.
 */
export function AssistActions({ surface, existing, onApply, onDiscard }: AssistActionsProps) {
	const { t } = useTranslation();
	const [mode, setMode] = useState<AssistMode | null>(null);

	const { data: modelsData } = useQuery(withResponseValidation(listLocalModelsOptions()));
	const loadedModelsQuery = useLoadedModels();

	// Same predicate the chat picker uses: installed, classified Chat, non-cloud provider.
	const models = useMemo(() => (modelsData?.items ?? []).filter(isLocalChatModel), [modelsData]);
	const loadedModelNames = useMemo(
		() => (loadedModelsQuery.data?.models ?? []).map((model) => model.modelName),
		[loadedModelsQuery.data],
	);

	const hasEligibleModel = models.length > 0;
	const canImprove = existing.content.trim().length > 0;

	return (
		<>
			<Group gap="xs" data-testid="assist-actions">
				{/* Mantine drops pointer events on a disabled Button, so the tooltip hangs off a wrapper instead. */}
				<Tooltip
					label={t("assist.noModelHint", "No local chat model is installed on this node, so nothing can draft yet.")}
					disabled={hasEligibleModel}
					multiline={true}
					w={260}
				>
					<Box>
						<Button
							variant="light"
							size="compact-sm"
							leftSection={<IconSparkles size={14} />}
							disabled={!hasEligibleModel}
							onClick={() => setMode("Create")}
							data-testid="assist-open-create"
						>
							{t("assist.draftButton", "Draft with AI")}
						</Button>
					</Box>
				</Tooltip>
				{canImprove ? (
					<Tooltip
						label={t("assist.noModelHint", "No local chat model is installed on this node, so nothing can draft yet.")}
						disabled={hasEligibleModel}
						multiline={true}
						w={260}
					>
						<Box>
							<Button
								variant="subtle"
								size="compact-sm"
								leftSection={<IconWand size={14} />}
								disabled={!hasEligibleModel}
								onClick={() => setMode("Improve")}
								data-testid="assist-open-improve"
							>
								{t("assist.improveButton", "Improve with AI")}
							</Button>
						</Box>
					</Tooltip>
				) : null}
			</Group>
			{mode ? (
				<GenerationAssistDialog
					// Re-key per mode so switching between Draft and Improve starts from a clean brief and result.
					key={mode}
					opened={true}
					surface={surface}
					mode={mode}
					existing={existing}
					models={models}
					loadedModelNames={loadedModelNames}
					onApply={onApply}
					onDiscard={onDiscard}
					onClose={() => setMode(null)}
				/>
			) : null}
		</>
	);
}
