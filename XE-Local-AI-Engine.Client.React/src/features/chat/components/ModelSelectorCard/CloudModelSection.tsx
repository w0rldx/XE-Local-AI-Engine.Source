import { Box, Group, Text } from "@mantine/core";
import type { ReactNode } from "react";

import type { ModelOption } from "@/core/models/ModelOption";
import { CloudModelOption } from "@/features/chat/components/ModelSelectorCard/CloudModelOption";

import classes from "../ModelSelectorCard.module.css";

interface CloudModelSectionProps {
	items: ModelOption[];
	title: string;
	egressCue: string;
	// Color of the egress cue. Orange (the default) means the turn leaves this network; a declared-local external
	// endpoint uses the dimmed color instead, because saying "Local network" in a warning color misreads.
	egressCueColor?: string;
	icon: ReactNode;
	selectedModel: string;
	onSelect: (value: string) => void;
}

export function CloudModelSection({
	items,
	title,
	egressCue,
	egressCueColor = "orange.6",
	icon,
	selectedModel,
	onSelect,
}: CloudModelSectionProps) {
	if (items.length === 0) {
		return null;
	}

	return (
		<Box>
			<Group gap={6} px="sm" py={6} wrap="nowrap">
				<Text size="xs" fw={700} c="dimmed" tt="uppercase" className={classes["dropdown-label"]} style={{ flex: 1 }}>
					{`${title} (${items.length})`}
				</Text>
				{icon}
			</Group>
			{items.map((option) => (
				<CloudModelOption
					key={option.value}
					option={option}
					selected={option.value === selectedModel}
					egressCue={egressCue}
					egressCueColor={egressCueColor}
					onSelect={onSelect}
				/>
			))}
		</Box>
	);
}
