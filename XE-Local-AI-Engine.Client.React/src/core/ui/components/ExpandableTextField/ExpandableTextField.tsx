import { Button, Stack, Text } from "@mantine/core";
import { IconArrowsDiagonal, IconArrowsMinimize } from "@tabler/icons-react";
import { useState } from "react";

export interface IExpandableTextFieldProps {
	/** Label shown before the preview text. */
	label: string;
	/** Full text value. The preview is clamped when collapsed. */
	value: string;
	/** Number of lines shown in the clamped preview. Defaults to 4. */
	previewLineClamp?: number;
	/** Label for the expand button. Defaults to "Show full". */
	expandLabel?: string;
	/** Label for the collapse button. Defaults to "Show less". */
	collapseLabel?: string;
}

/**
 * Renders a potentially long text value as a clamped, minimized preview with a
 * toggle button that expands/collapses the full text inline.
 *
 * Reusable across the app for any text that can grow large (model licenses,
 * templates, system prompts, …) so it never blows out the surrounding layout.
 * Collapsed by default — the full content is only shown after the user explicitly
 * activates the toggle.
 */
export function ExpandableTextField({
	label,
	value,
	previewLineClamp = 4,
	expandLabel = "Show full",
	collapseLabel = "Show less",
}: IExpandableTextFieldProps) {
	const [expanded, setExpanded] = useState(false);

	return (
		<Stack gap={4}>
			<Text
				size="sm"
				c="dimmed"
				lineClamp={expanded ? undefined : previewLineClamp}
				style={{ whiteSpace: "pre-wrap" }}
			>
				{label}: {value}
			</Text>
			<Button
				variant="subtle"
				size="compact-sm"
				leftSection={expanded ? <IconArrowsMinimize size={14} /> : <IconArrowsDiagonal size={14} />}
				onClick={() => setExpanded((prev) => !prev)}
				aria-expanded={expanded}
				style={{ alignSelf: "flex-start" }}
			>
				{expanded ? collapseLabel : expandLabel}
			</Button>
		</Stack>
	);
}
