import { Box, Group, type MantineSize, type MantineSpacing, Text } from "@mantine/core";
import type { ReactNode } from "react";

interface LabelValueRowProps {
	label: ReactNode;
	children: ReactNode;
	/** Fixed label-column width, so every row's value starts at the same x (detail dialogs). */
	labelWidth?: number | string;
	/** "space-between" pushes the value to the far edge and right-aligns it (detail drawers). */
	justify?: "flex-start" | "space-between";
	/** Label scale; the right-aligned value follows it so a bare string child matches the label. */
	size?: MantineSize;
	/** Cross-axis alignment. "flex-start" keeps the label on the first line when the value wraps to several. */
	align?: "center" | "flex-start";
	/** Space between the label and its value. */
	gap?: MantineSpacing;
}

// Standard label/value row for read-only detail panels, dialogs and drawers. The label never shrinks, so a long
// value wraps or truncates instead of squeezing the label out of alignment with the rows above it.
export function LabelValueRow({
	label,
	children,
	labelWidth,
	justify = "flex-start",
	size = "sm",
	align = "center",
	gap = "sm",
}: LabelValueRowProps) {
	return (
		<Group gap={gap} align={align} wrap="nowrap" justify={justify}>
			<Text size={size} c="dimmed" w={labelWidth} style={{ flexShrink: 0 }}>
				{label}
			</Text>
			{justify === "space-between" ? (
				<Box fz={size} ta="right" style={{ minWidth: 0 }}>
					{children}
				</Box>
			) : (
				children
			)}
		</Group>
	);
}
