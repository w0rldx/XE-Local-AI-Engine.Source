import { Button, Group, Paper, Stack, Table, Text, UnstyledButton } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { shortTensorName, tensorSizeLabel } from "@/core/models/inspect/GgufInspectInsights";
import { type ArchitectureGroup, componentLabel } from "@/features/modelLab/models/ModelLabArchitecture";

interface ArchitectureTensorGroupsProps {
	groups: ArchitectureGroup[];
	blockIndex: number | undefined;
	selectedComponentId: string | null;
	selectedTensorName: string | null;
	onSelectGroup: (componentId: string) => void;
	onSelectTensor: (componentId: string, ggufName: string) => void;
}

// The tensors of each component as read from the file; a group or a tensor is selected by keyboard as well as by pointer.
export function ArchitectureTensorGroups({
	groups,
	blockIndex,
	selectedComponentId,
	selectedTensorName,
	onSelectGroup,
	onSelectTensor,
}: ArchitectureTensorGroupsProps) {
	const { t } = useTranslation();
	return (
		<Stack gap="xs">
			{groups.map((group) => {
				const selected = group.componentId === selectedComponentId;
				return (
					<Paper
						key={group.componentId}
						withBorder={true}
						p="xs"
						data-testid={`model-lab-architecture-group-${group.componentId}`}
						data-selected={selected}
						style={selected ? { borderColor: "var(--mantine-primary-color-filled)" } : undefined}
					>
						<Stack gap={4}>
							<UnstyledButton aria-pressed={selected} onClick={() => onSelectGroup(group.componentId)}>
								<Group gap="xs">
									<Text size="sm" fw={600}>
										{componentLabel(t, group.component)}
									</Text>
									<Text size="xs" c="dimmed" ff="monospace">
										{group.componentId}
									</Text>
								</Group>
							</UnstyledButton>
							<Table fz="xs" withRowBorders={false}>
								<Table.Tbody>
									{group.tensors.map((tensor) => (
										<Table.Tr key={tensor.ggufName}>
											<Table.Td>
												<Button
													variant={tensor.ggufName === selectedTensorName ? "light" : "subtle"}
													size="compact-xs"
													ff="monospace"
													aria-pressed={tensor.ggufName === selectedTensorName}
													onClick={() => onSelectTensor(group.componentId, tensor.ggufName)}
												>
													{shortTensorName(tensor.ggufName, blockIndex)}
												</Button>
											</Table.Td>
											<Table.Td ff="monospace">{tensor.shape.join(" × ")}</Table.Td>
											<Table.Td>{tensor.ggmlType ?? t("pages.models.details.unknown", "Unknown")}</Table.Td>
											<Table.Td>{tensorSizeLabel(t, tensor.byteSize)}</Table.Td>
										</Table.Tr>
									))}
								</Table.Tbody>
							</Table>
						</Stack>
					</Paper>
				);
			})}
		</Stack>
	);
}
