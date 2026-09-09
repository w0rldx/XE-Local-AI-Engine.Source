import { Badge, Code, Group, Paper, ScrollArea, SimpleGrid, Stack, Text } from "@mantine/core";
import type { ReactNode } from "react";

import { StatTile } from "@/core/ui/components/StatTile/StatTile";

/**
 * The per-command evidence a deterministic-validation report carries, reduced to the fields this card renders.
 *
 * Structural on purpose: Dev Mode and Dev Workflows each own their report contract, and neither may import the
 * other's. Both command records are assignable to this without either side knowing it exists.
 */
export interface ValidationCommandSummary {
	readonly commandId: string;
	readonly exitCode: number;
	readonly completed: boolean;
	readonly outputTruncated: boolean;
	readonly durationMilliseconds: number;
	readonly standardOutput: string;
	readonly standardError: string;
	readonly testOutcome: ValidationTestOutcomeSummary | null;
}

/** The counts half of a test outcome. The parse-failure half is rendered by the caller — see `unparsedOutcome`. */
interface ValidationTestOutcomeSummary {
	readonly parsed: boolean;
	readonly discovered: number;
	readonly executed: number;
	readonly passed: number;
	readonly failed: number;
}

/**
 * Operator-facing text, ALREADY translated. The two callers key their strings under their own `pages.*` namespace and
 * word them differently ("Tests executed" against "Executed"), so this takes resolved strings rather than i18n keys.
 */
export interface ValidationCommandCardLabels {
	readonly incomplete: string;
	readonly truncated: string;
	/** Carries the code itself, e.g. "exit 1": the two callers interpolate it differently. */
	readonly exitCode: string;
	/** Caption over the captured output. Omitted renders the output block with no caption. */
	readonly capturedOutput?: string;
	readonly testDiscovered: string;
	readonly testExecuted: string;
	readonly testPassed: string;
	readonly testFailed: string;
}

/** Test ids are a public contract on both sides and do not follow one scheme, so every one of them is given. */
export interface ValidationCommandCardTestIds {
	readonly card: string;
	readonly output: string;
	readonly testCounts: string;
	/** Prefixes the four count VALUES: a test that could only find the tile would pass against four zeroes. */
	readonly testCountValuePrefix: string;
}

interface ValidationCommandCardProps {
	readonly command: ValidationCommandSummary;
	readonly labels: ValidationCommandCardLabels;
	readonly testIds: ValidationCommandCardTestIds;
	/**
	 * Rendered in place of the counts when the outcome could not be parsed. A parse failure is a validation failure,
	 * never missing data — so it replaces the counts rather than sitting beside them — but what it says diverges per
	 * feature (Dev Mode reads "no test projects" as a policy case, Dev Workflows shows the server's sentence), so the
	 * caller owns that alert.
	 */
	readonly unparsedOutcome?: ReactNode;
}

// One command's evidence inside a validation report: its id, how it ended, and — only when it failed — what it printed.
export function ValidationCommandCard({ command, labels, testIds, unparsedOutcome }: ValidationCommandCardProps) {
	const failed = !command.completed || command.exitCode !== 0;
	// Only a failing command's captured output is rendered. On a pass it is noise; on a failure it is the ONLY record
	// of why — the live evaluation had to read the artifact blob out of the API by hand to find `errno == EROFS`,
	// because the panel showed an exit code and nothing else. It is already sanitized server-side, and when the whole
	// report would not fit it is the server's own sentence saying so, which is why it is printed verbatim.
	const capturedOutput = failed ? [command.standardError, command.standardOutput].filter((text) => !!text?.trim()) : [];

	return (
		<Paper withBorder={true} p="sm" data-testid={testIds.card}>
			<Group justify="space-between" wrap="nowrap" align="flex-start">
				<Code>{command.commandId}</Code>
				<Group gap="xs" wrap="wrap">
					{command.completed ? null : <Badge color="red">{labels.incomplete}</Badge>}
					{command.outputTruncated ? (
						<Badge color="yellow" variant="light">
							{labels.truncated}
						</Badge>
					) : null}
					<Badge color={failed ? "red" : "green"} variant="light">
						{labels.exitCode}
					</Badge>
					<Text size="xs" c="dimmed">
						{((command.durationMilliseconds ?? 0) / 1000).toFixed(1)}s
					</Text>
				</Group>
			</Group>
			{command.testOutcome ? (
				<TestCounts outcome={command.testOutcome} labels={labels} testIds={testIds} unparsedOutcome={unparsedOutcome} />
			) : null}
			{capturedOutput.length > 0 ? (
				<Stack gap={4} mt="sm">
					{labels.capturedOutput === undefined ? null : (
						<Text size="xs" c="dimmed">
							{labels.capturedOutput}
						</Text>
					)}
					<ScrollArea.Autosize mah={220}>
						<Code block={true} data-testid={testIds.output}>
							{capturedOutput.join("\n")}
						</Code>
					</ScrollArea.Autosize>
				</Stack>
			) : null}
		</Paper>
	);
}

function TestCounts({
	outcome,
	labels,
	testIds,
	unparsedOutcome,
}: {
	readonly outcome: ValidationTestOutcomeSummary;
	readonly labels: ValidationCommandCardLabels;
	readonly testIds: ValidationCommandCardTestIds;
	readonly unparsedOutcome: ReactNode;
}) {
	if (!outcome.parsed) {
		return unparsedOutcome;
	}

	return (
		<SimpleGrid cols={{ base: 2, sm: 4 }} mt="sm" data-testid={testIds.testCounts}>
			<StatTile
				variant="paper"
				label={labels.testDiscovered}
				value={outcome.discovered}
				valueTestId={`${testIds.testCountValuePrefix}-discovered`}
			/>
			<StatTile
				variant="paper"
				label={labels.testExecuted}
				value={outcome.executed}
				valueTestId={`${testIds.testCountValuePrefix}-executed`}
			/>
			<StatTile
				variant="paper"
				label={labels.testPassed}
				value={outcome.passed}
				valueTestId={`${testIds.testCountValuePrefix}-passed`}
			/>
			<StatTile
				variant="paper"
				label={labels.testFailed}
				value={outcome.failed}
				valueTestId={`${testIds.testCountValuePrefix}-failed`}
			/>
		</SimpleGrid>
	);
}
