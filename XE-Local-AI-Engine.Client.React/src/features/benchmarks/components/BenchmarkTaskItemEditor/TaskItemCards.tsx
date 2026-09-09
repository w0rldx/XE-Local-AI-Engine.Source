import { ActionIcon, Badge, Card, Collapse, Group, Stack, Text } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconChevronUp, IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import type { BenchmarkTaskItem } from "@/features/benchmarks/models/BenchmarkTaskItems";
import { benchmarkNiahCaseLabel } from "@/features/benchmarks/models/BenchmarkTaskItems";

interface TaskItemCardsProps {
	groups: BenchmarkTaskItem[][];
	expanded: ReadonlySet<string>;
	isBusy: boolean;
	onToggleExpanded: (itemId: string) => void;
	onMove: (item: BenchmarkTaskItem, direction: -1 | 1) => void;
	onOpen: (item: BenchmarkTaskItem) => void;
	onDelete: (item: BenchmarkTaskItem) => void;
}

/** The project's task items as ordered cards: a generator's group leader carries its expandable generated cases. */
export function TaskItemCards({ groups, expanded, isBusy, onToggleExpanded, onMove, onOpen, onDelete }: TaskItemCardsProps) {
	const { t } = useTranslation();
	return (
		<>
			{groups.map(([item, ...cases], groupIndex) => {
				const parent = item as BenchmarkTaskItem;
				const isOpen = expanded.has(parent.id);
				return (
					<Card key={parent.id} withBorder={true} padding="xs" data-testid={`benchmark-item-${parent.id}`}>
						<Group justify="space-between" wrap="nowrap" align="flex-start">
							<Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
								<Badge variant="light">{groupIndex + 1}</Badge>
								<Stack gap={2} style={{ minWidth: 0 }}>
									<Text size="sm" truncate="end" data-testid={`benchmark-item-prompt-${parent.id}`}>
										{parent.prompt}
									</Text>
									<Group gap={4} wrap="nowrap">
										{parent.kind === "niah" ? (
											<StatusBadge
												color="grape"
												label={t("pages.benchmarks.items.caseCount", "{{count}} probe cases", { count: cases.length })}
												data-testid={`benchmark-item-cases-${parent.id}`}
											/>
										) : null}
										{parent.countsTowardScore ? null : (
											<StatusBadge
												color="gray"
												label={t("pages.benchmarks.items.notScored", "own axis")}
												data-testid={`benchmark-item-unscored-${parent.id}`}
											/>
										)}
										{parent.verifierConfig === null ? null : (
											<StatusBadge
												color="blue"
												label={t("pages.benchmarks.items.hasOverride", "verifier override")}
												data-testid={`benchmark-item-override-${parent.id}`}
											/>
										)}
										<Text size="xs" c="dimmed">
											{t("pages.benchmarks.items.revision", "r{{revision}}", { revision: parent.revision })}
										</Text>
									</Group>
								</Stack>
							</Group>
							<Group gap={2} wrap="nowrap">
								{cases.length > 0 ? (
									<ActionIcon
										variant="subtle"
										size="sm"
										aria-label={t("pages.benchmarks.items.showCases", "Show the generated probe cases")}
										aria-expanded={isOpen}
										onClick={() => onToggleExpanded(parent.id)}
										data-testid={`benchmark-item-cases-toggle-${parent.id}`}
									>
										{isOpen ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
									</ActionIcon>
								) : null}
								<ActionIcon
									variant="subtle"
									size="sm"
									disabled={isBusy || groupIndex === 0}
									aria-label={t("pages.benchmarks.items.moveUp", "Move up")}
									onClick={() => onMove(parent, -1)}
									data-testid={`benchmark-item-up-${parent.id}`}
								>
									<IconChevronUp size={14} />
								</ActionIcon>
								<ActionIcon
									variant="subtle"
									size="sm"
									disabled={isBusy || groupIndex === groups.length - 1}
									aria-label={t("pages.benchmarks.items.moveDown", "Move down")}
									onClick={() => onMove(parent, 1)}
									data-testid={`benchmark-item-down-${parent.id}`}
								>
									<IconChevronDown size={14} />
								</ActionIcon>
								<ActionIcon
									variant="subtle"
									size="sm"
									disabled={isBusy}
									aria-label={t("common.edit", "Edit")}
									onClick={() => onOpen(parent)}
									data-testid={`benchmark-item-edit-${parent.id}`}
								>
									<IconPencil size={14} />
								</ActionIcon>
								<ActionIcon
									variant="subtle"
									size="sm"
									color="red"
									// A project always holds at least one item; the node refuses the last one anyway.
									disabled={isBusy || groups.length <= 1}
									aria-label={t("common.delete", "Delete")}
									onClick={() => onDelete(parent)}
									data-testid={`benchmark-item-delete-${parent.id}`}
								>
									<IconTrash size={14} />
								</ActionIcon>
							</Group>
						</Group>
						{cases.length > 0 ? (
							<Collapse expanded={isOpen}>
								<Stack gap={2} mt="xs" pl="md">
									{cases.map((generated) => (
										<Text key={generated.id} size="xs" c="dimmed" data-testid={`benchmark-item-case-${generated.id}`}>
											{benchmarkNiahCaseLabel(generated) ?? generated.prompt.slice(0, 60)}
										</Text>
									))}
								</Stack>
							</Collapse>
						) : null}
					</Card>
				);
			})}
		</>
	);
}
