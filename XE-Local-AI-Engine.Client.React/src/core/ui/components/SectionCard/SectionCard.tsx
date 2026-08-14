import { Card, Group, type MantineSpacing, Stack, Title } from "@mantine/core";
import type { ReactNode } from "react";

interface SectionCardProps {
	children: ReactNode;
	/** Section heading rendered as an h3; omit for chrome-less content cards. */
	title?: ReactNode;
	/** Trailing decoration on the heading row; by convention a Tabler icon with size={22}. */
	icon?: ReactNode;
	/** Right-aligned heading-row actions (badges, buttons). Rendered before the icon. */
	actions?: ReactNode;
	/** Gap between the card's stacked children. Defaults to the app-wide "md". */
	gap?: MantineSpacing;
	"data-tour"?: string;
	"data-testid"?: string;
}

// Standard content section: bordered card with an optional h3 heading row. Pages compose their body
// from these so section chrome (border, radius, padding, heading level) is identical everywhere.
export function SectionCard({
	children,
	title,
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
						{title !== undefined ? <Title order={3}>{title}</Title> : null}
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
