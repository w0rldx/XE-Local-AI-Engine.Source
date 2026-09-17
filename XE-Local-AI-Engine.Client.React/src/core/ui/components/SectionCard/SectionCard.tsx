import { Card, Group, type MantineSpacing, Stack, Title } from "@mantine/core";
import type { ReactNode } from "react";

interface SectionCardProps {
	children: ReactNode;
	/** Section heading rendered as an h2 at the h3 type scale; omit for chrome-less content cards. */
	title?: ReactNode;
	/**
	 * Outline level of the heading. A section sits directly under the page's h1, so the default 2 is right almost
	 * everywhere; pass 3 only for a card nested inside another SectionCard (the charts card under Runs on /benchmarks),
	 * where an h2 would claim to be a sibling of the section that contains it. The type scale is pinned either way.
	 */
	titleOrder?: 2 | 3;
	/** Trailing decoration on the heading row; by convention a Tabler icon with size={22}. */
	icon?: ReactNode;
	/** Right-aligned heading-row actions (badges, buttons). Rendered before the icon. */
	actions?: ReactNode;
	/** Gap between the card's stacked children. Defaults to the app-wide "md". */
	gap?: MantineSpacing;
	"data-tour"?: string;
	"data-testid"?: string;
}

// Standard content section: bordered card with an optional heading row. Pages compose their body
// from these so section chrome (border, radius, padding, heading level) is identical everywhere.
export function SectionCard({
	children,
	title,
	titleOrder = 2,
	icon,
	actions,
	gap = "md",
	"data-tour": dataTour,
	"data-testid": testId,
}: SectionCardProps) {
	const hasHeadingRow = title !== undefined || icon !== undefined || actions !== undefined;

	return (
		<Card withBorder={true} radius="md" p="lg" data-tour={dataTour} data-testid={testId}>
			<Stack gap={gap}>
				{hasHeadingRow ? (
					<Group justify={title !== undefined ? "space-between" : "flex-end"} align="center">
						{title !== undefined ? (
							<Title order={titleOrder} size="h3">
								{title}
							</Title>
						) : null}
						{actions !== undefined || icon !== undefined ? (
							<Group gap="sm" align="center">
								{actions}
								{icon}
							</Group>
						) : null}
					</Group>
				) : null}
				{children}
			</Stack>
		</Card>
	);
}
