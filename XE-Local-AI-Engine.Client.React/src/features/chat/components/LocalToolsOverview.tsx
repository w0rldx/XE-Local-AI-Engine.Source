import { Badge, Group, Paper, Stack, Text } from "@mantine/core";
import { IconCalculator, IconClock } from "@tabler/icons-react";
import type { ReactNode } from "react";

import type { LocalToolDescriptor } from "@/features/chat/models/LocalToolCatalog";
import { localToolCatalog } from "@/features/chat/models/LocalToolCatalog";

function toolIcon(name: string): ReactNode {
	if (name === "GetCurrentTime") {
		return <IconClock size={14} />;
	}
	if (name === "Calculate") {
		return <IconCalculator size={14} />;
	}
	return null;
}

interface LocalToolRowProps {
	tool: LocalToolDescriptor;
}

function LocalToolRow({ tool }: LocalToolRowProps) {
	return (
		<Paper withBorder={true} p="xs" data-testid={`local-tool-row-${tool.name}`}>
			<Stack gap={4}>
				<Group gap="xs" wrap="nowrap" align="center">
					{toolIcon(tool.name)}
					<Text size="sm" fw={600} ff="monospace" style={{ flex: 1 }}>
						{tool.name}
					</Text>
					<Badge
						size="xs"
						variant="light"
						color={tool.requiresApproval ? "orange" : "teal"}
						data-testid={`local-tool-approval-badge-${tool.name}`}
					>
						{tool.requiresApproval ? "requires approval" : "auto-execute"}
					</Badge>
				</Group>
				<Text size="xs" c="dimmed">
					{tool.description}
				</Text>
			</Stack>
		</Paper>
	);
}

export function LocalToolsOverview() {
	return (
		<Paper withBorder={true} p="sm" data-testid="local-tools-overview">
			<Stack gap="xs">
				<Group justify="space-between" align="center">
					<Text size="sm" fw={600}>
						Local tools
					</Text>
					<Badge size="xs" variant="dot" color="teal">
						{localToolCatalog.length} available
					</Badge>
				</Group>
				{localToolCatalog.map((tool) => (
					<LocalToolRow key={tool.name} tool={tool} />
				))}
				<Text size="xs" c="dimmed">
					Tools run in-process on this node. No external access.
				</Text>
			</Stack>
		</Paper>
	);
}
