import { Alert, Group, Loader, Paper, ScrollArea, Stack, Text, Title } from "@mantine/core";
import { memo, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type {
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabPromptTokenResponse as PromptToken,
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabPromptTokensResponse as PromptTokens,
} from "@/core/api/generated/types.gen";
import { logprobTextLabel } from "@/core/directInference/DirectInferenceModels";
import { BasisBadge } from "@/features/modelLab/components/ArchitectureExplanation";
import classes from "@/features/modelLab/components/NextTokenPromptTokens.module.css";
import { moveFocusAmongButtons, useRovingTabStop } from "@/features/modelLab/models/ModelLabFocus";

const visibleWhitespace: Record<string, string> = { " ": "·", "\n": "↵", "\t": "⇥", "\r": "␍" };

// A space or a newline token would otherwise be a blank chip.
function visiblePiece(piece: string): string {
	return piece === "" ? "∅" : piece.replace(/[ \n\t\r]/g, (character) => visibleWhitespace[character] ?? character);
}

// The server refuses with ModelNotLoaded when the model was unloaded or its server went away: reading never loads it.
function isModelNotLoaded(error: unknown): boolean {
	return (
		error instanceof ApiError &&
		error.statusCode === 409 &&
		(error.apiProblemDetails as unknown as Record<string, unknown> | undefined)?.["reason"] === "ModelNotLoaded"
	);
}

interface TokenChipsProps {
	tokens: readonly PromptToken[];
	label: string;
	onActivate: (index: number) => void;
}

// Memoized: pointing at a chip changes the readout, not the up to several thousand chips.
const TokenChips = memo(function TokenChips({ tokens, label, onActivate }: TokenChipsProps) {
	const { t } = useTranslation();
	// One Tab stop, on the first chip until focus moves; the arrow keys move between the others.
	const stop = useRovingTabStop(-1, tokens.length);
	return (
		<Group gap={2} role="group" aria-label={label} onKeyDown={moveFocusAmongButtons}>
			{tokens.map((token, index) => {
				const values = { piece: logprobTextLabel(token.piece), id: token.id };
				return (
					<button
						// biome-ignore lint/suspicious/noArrayIndexKey: tokens repeat, and a run's list never reorders.
						key={index}
						type="button"
						tabIndex={stop.tabIndexFor(index)}
						className={classes["chip"]}
						data-special={token.special}
						aria-label={
							token.special
								? t("pages.modelLab.nextToken.ui.promptTokens.chipSpecial", "{{piece}}, id {{id}}, control token", values)
								: t("pages.modelLab.nextToken.ui.promptTokens.chip", "{{piece}}, id {{id}}", values)
						}
						onFocus={() => {
							stop.onFocusAt(index);
							onActivate(index);
						}}
						onMouseEnter={() => onActivate(index)}
						onClick={() => onActivate(index)}
						data-testid="model-lab-prompt-token"
					>
						{visiblePiece(token.piece)}
					</button>
				);
			})}
		</Group>
	);
});

interface NextTokenPromptTokensProps {
	pending: boolean;
	data: PromptTokens | undefined;
	error: unknown;
}

// The run's prompt as the model read it: the chat template applied, then tokenized by the loaded llama.cpp server.
export function NextTokenPromptTokens({ pending, data, error }: NextTokenPromptTokensProps) {
	const { t } = useTranslation();
	const [active, setActive] = useState<number | null>(null);
	const activeToken = active === null ? undefined : data?.tokens[active];
	const readout = activeToken && {
		position: (active ?? 0) + 1,
		total: data?.tokens.length,
		piece: logprobTextLabel(activeToken.piece),
		id: activeToken.id,
	};
	const title = t("pages.modelLab.nextToken.ui.promptTokens.title", "Prompt tokens");
	return (
		<Paper withBorder={true} p="md" data-testid="model-lab-prompt-tokens">
			<Stack gap="sm">
				<Group gap="xs" justify="space-between">
					<Title order={5}>{title}</Title>
					{/* Measured only once the tokens arrived: no claim while they load or after the read failed. */}
					{data && <BasisBadge basis="measuredLlamaCpp" />}
				</Group>
				{/* The server checks each distinct token on its own, so a long prompt takes a moment. */}
				{pending && (
					<Group gap="xs" data-testid="model-lab-prompt-tokens-loading">
						<Loader size="sm" />
						<Text size="sm" c="dimmed">
							{t(
								"pages.modelLab.nextToken.ui.promptTokens.loading",
								"Reading the prompt's tokens from llama.cpp. A long prompt can take a moment.",
							)}
						</Text>
					</Group>
				)}
				{error !== null &&
					(isModelNotLoaded(error) ? (
						<Text size="sm" c="dimmed" data-testid="model-lab-prompt-tokens-not-loaded">
							{t(
								"pages.modelLab.nextToken.ui.promptTokens.notLoaded",
								"The model was unloaded, or its server stopped, before its prompt tokens could be read. Run again to see them.",
							)}
						</Text>
					) : (
						<Alert color="red" data-testid="model-lab-prompt-tokens-failed">
							{apiErrorMessage(
								error,
								t("pages.modelLab.nextToken.ui.promptTokens.failed", "The prompt tokens could not be read."),
							)}
						</Alert>
					))}
				{data && (
					<>
						<Text size="sm" data-testid="model-lab-prompt-tokens-count">
							{t(
								"pages.modelLab.nextToken.ui.promptTokens.count",
								"The model read {{tokens}} tokens: your prompt with the chat template applied.",
								{ tokens: data.tokenCount },
							)}
						</Text>
						{data.truncated && (
							<Text size="sm" c="dimmed" data-testid="model-lab-prompt-tokens-truncated">
								{t("pages.modelLab.nextToken.ui.promptTokens.truncated", "Only the first {{shown}} tokens are shown.", {
									shown: data.tokens.length,
								})}
							</Text>
						)}
						<Text size="xs" c="dimmed">
							{t(
								"pages.modelLab.nextToken.ui.promptTokens.legend",
								"Tokens llama.cpp parses as control tokens have a dashed outline. · marks a space, ↵ a newline, ⇥ a tab.",
							)}
						</Text>
						<ScrollArea.Autosize mah={240}>
							<TokenChips tokens={data.tokens} label={title} onActivate={setActive} />
						</ScrollArea.Autosize>
						<Text size="sm" ff="monospace" aria-live="polite" data-testid="model-lab-prompt-tokens-readout">
							{readout
								? activeToken?.special
									? t(
											"pages.modelLab.nextToken.ui.promptTokens.readoutSpecial",
											"Token {{position}} of {{total}}: {{piece}}, id {{id}}, control token",
											readout,
										)
									: t(
											"pages.modelLab.nextToken.ui.promptTokens.readout",
											"Token {{position}} of {{total}}: {{piece}}, id {{id}}",
											readout,
										)
								: t(
										"pages.modelLab.nextToken.ui.promptTokens.pick",
										"Point at a token, or move to it with the arrow keys, to see its id.",
									)}
						</Text>
					</>
				)}
			</Stack>
		</Paper>
	);
}
