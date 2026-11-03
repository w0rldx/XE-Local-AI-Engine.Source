import { Alert, Badge, Group, ScrollArea, Stack, Table, Text, Title } from "@mantine/core";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import {
	type DirectInferenceResult,
	directInferenceMaxKeptLogprobEntries,
	logprobPercentLabel,
	logprobTextLabel,
} from "@/core/directInference/DirectInferenceModels";

interface DirectInferenceLogprobsProps {
	result: DirectInferenceResult;
	running: boolean;
}

// Entries are read as they came: never counted as tokens or joined into the text, because the stream merges and omits them.
export function DirectInferenceLogprobs({ result, running }: DirectInferenceLogprobsProps) {
	const { t } = useTranslation();
	const entries = result.logprobs;
	const entryCount = entries?.length ?? 0;
	const outputTokens = result.usage?.outputTokens;
	const reasoningLeftOut = result.reasoningLogprobsLeftOut ?? 0;
	const answerLeftOut = result.answerLogprobsLeftOut ?? 0;
	const hasEntries = entryCount > 0;

	// Every streamed event re-renders the page; the rows only change when an entry was kept.
	const table = useMemo(
		() =>
			entries && entries.length > 0 ? (
				<ScrollArea.Autosize mah={360}>
					<Table striped={true} withTableBorder={true} fz="xs" miw={640}>
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t("pages.directInference.logprobs.columns.text", "Entry")}</Table.Th>
								<Table.Th>{t("pages.directInference.logprobs.columns.probability", "Probability")}</Table.Th>
								<Table.Th>{t("pages.directInference.logprobs.columns.alternatives", "Alternatives")}</Table.Th>
							</Table.Tr>
						</Table.Thead>
						<Table.Tbody>
							{entries.map((entry, index) => (
								// The stream only appends, so a position is a stable identity here.
								// biome-ignore lint/suspicious/noArrayIndexKey: see above.
								<Table.Tr key={index} data-testid="direct-inference-logprob-entry">
									<Table.Td ff="monospace" style={{ whiteSpace: "pre-wrap" }}>
										<Group gap={6} wrap="nowrap">
											{logprobTextLabel(entry.text)}
											{entry.isReasoning && (
												<Badge size="xs" variant="light" color="gray">
													{t("pages.directInference.logprobs.thinking", "Thinking")}
												</Badge>
											)}
										</Group>
									</Table.Td>
									<Table.Td>{logprobPercentLabel(entry.logprob)}</Table.Td>
									<Table.Td ff="monospace" style={{ whiteSpace: "pre-wrap" }}>
										{entry.alternatives
											.map((alternative) => `${logprobTextLabel(alternative.text)} ${logprobPercentLabel(alternative.logprob)}`)
											.join("  ")}
									</Table.Td>
								</Table.Tr>
							))}
						</Table.Tbody>
					</Table>
				</ScrollArea.Autosize>
			) : null,
		[entries, t],
	);

	return (
		<Stack gap="xs" data-testid="direct-inference-logprobs">
			<Title order={5}>{t("pages.directInference.logprobs.title", "Token probabilities")}</Title>
			<Alert color="blue">
				<Text size="sm">
					{t(
						"pages.directInference.logprobs.explanation",
						"Probabilities the model assigned before sampling. They are model output, not an explanation of its reasoning, and a sampled pick can look unlikely.",
					)}
				</Text>
				<Text size="sm">
					{t(
						"pages.directInference.logprobs.entriesNote",
						"Entries are not tokens. Multi-byte text can merge several tokens into one entry, whose values belong to its last token, and entries can be missing, especially with thinking on. So the list may not match the text or the token counts.",
					)}
				</Text>
			</Alert>
			{!hasEntries && result.elapsedMs !== undefined && (
				<Text size="sm" c="dimmed" data-testid="direct-inference-logprobs-none">
					{t("pages.directInference.logprobs.none", "The server returned no probabilities for this request.")}
				</Text>
			)}
			{table}
			{hasEntries && outputTokens != null && !running && (
				<Text size="xs" c="dimmed" data-testid="direct-inference-logprobs-count">
					{t(
						"pages.directInference.logprobs.countNote",
						"Entries: {{entries}}. Generated tokens the server reported: {{tokens}}.",
						{ entries: entryCount + reasoningLeftOut + answerLeftOut, tokens: outputTokens },
					)}
				</Text>
			)}
			{reasoningLeftOut > 0 && (
				<Text size="xs" c="dimmed" data-testid="direct-inference-logprobs-left-out-reasoning">
					{t(
						"pages.directInference.logprobs.leftOutReasoning",
						"Thinking: showing the first {{kept}} entries; {{leftOut}} more were left out.",
						{ kept: directInferenceMaxKeptLogprobEntries, leftOut: reasoningLeftOut },
					)}
				</Text>
			)}
			{answerLeftOut > 0 && (
				<Text size="xs" c="dimmed" data-testid="direct-inference-logprobs-left-out-answer">
					{t(
						"pages.directInference.logprobs.leftOutAnswer",
						"Answer: showing the first {{kept}} entries; {{leftOut}} more were left out.",
						{ kept: directInferenceMaxKeptLogprobEntries, leftOut: answerLeftOut },
					)}
				</Text>
			)}
		</Stack>
	);
}
