import { Card, Paper, Stack, Text, Tooltip } from "@mantine/core";
import type { ReactNode } from "react";

export type StatTileVariant = "card" | "plain" | "paper";

interface StatTileProps {
	label: ReactNode;
	value: ReactNode;
	/**
	 * Chrome around the tile: "card" is the headline dashboard tile, "paper" the compact bordered tile used inside a
	 * panel, and "plain" the unframed metric used where the surrounding card already supplies the border.
	 */
	variant?: StatTileVariant;
	/** Unabbreviated value, surfaced as a hover Tooltip and as the headline's aria-label. */
	exactValue?: string;
	/** Test id on the tile wrapper. */
	"data-testid"?: string;
	/**
	 * Test id on the value text itself. Prefer it over the wrapper id wherever the number is the assertion: a test that
	 * can only locate the tile proves a value rendered, not which one.
	 */
	valueTestId?: string;
}

const variantStyles = {
	card: { labelSize: "sm", valueSize: "xl", valueWeight: 700 },
	plain: { labelSize: "xs", valueSize: "sm", valueWeight: 500 },
	paper: { labelSize: "xs", valueSize: undefined, valueWeight: 600 },
} as const;

// Standard labelled metric tile: dimmed caption over an emphasised value, in the three frames the app uses.
export function StatTile({
	label,
	value,
	variant = "plain",
	exactValue,
	"data-testid": testId,
	valueTestId,
}: StatTileProps) {
	const style = variantStyles[variant];

	const caption = (
		<Text size={style.labelSize} c="dimmed">
			{label}
		</Text>
	);
	const headline = (
		<Text size={style.valueSize} fw={style.valueWeight} aria-label={exactValue} data-testid={valueTestId}>
			{value}
		</Text>
	);
	const body =
		exactValue === undefined ? (
			headline
		) : (
			<Tooltip label={exactValue} withArrow={true}>
				{headline}
			</Tooltip>
		);

	if (variant === "card") {
		return (
			<Card withBorder={true} radius="md" p="lg" data-testid={testId}>
				<Stack gap={4}>
					{caption}
					{body}
				</Stack>
			</Card>
		);
	}

	if (variant === "paper") {
		return (
			<Paper withBorder={true} p="sm" data-testid={testId}>
				{caption}
				{body}
			</Paper>
		);
	}

	return (
		<Stack gap={0} data-testid={testId}>
			{caption}
			{body}
		</Stack>
	);
}
