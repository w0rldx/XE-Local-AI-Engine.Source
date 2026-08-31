import { Group, Select, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentTaskDetailResponse as DevelopmentTaskDetail } from "@/core/api/generated/types.gen";

export interface DevelopmentTaskSwitcherProps {
	readonly tasks: readonly DevelopmentTaskDetail[];
	readonly selectedTaskId: string | null;
	readonly onSelect: (taskId: string) => void;
}

/**
 * Which of a project's tasks the page is showing.
 *
 * A project used to carry exactly one task, so there was nothing to switch between and the page simply took the first
 * row. Phase W dropped that unique index: a workflow can decompose one request into a task per child, and the ordinary
 * decomposed case is three tasks in one project — two of which had no way to be reached from this page at all.
 *
 * Rendered only when there IS a choice. A single-task project gets no control, because a picker with one entry is a
 * question with one answer.
 *
 * The order is the server's (`ListTasksAsync` orders by CreatedAtUtc), which is load-bearing rather than incidental:
 * the first row is the operator's own task, and the executor relies on that for the acceptance criteria and review
 * budget a materialized child inherits. This renders that order and does not reorder it.
 */
export function DevelopmentTaskSwitcher({ tasks, selectedTaskId, onSelect }: DevelopmentTaskSwitcherProps) {
	const { t } = useTranslation();
	if (tasks.length < 2) {
		return null;
	}

	const options = tasks.flatMap((entry, index) => {
		const id = entry.task?.id;
		return id
			? [
					{
						value: id,
						// A workflow-created child inherits a title; one that somehow has none is still selectable by its
						// position, which is the only other thing that distinguishes it.
						label: entry.task?.title || t("pages.development.tasks.untitled", "Task {{index}}", { index: index + 1 }),
					},
				]
			: [];
	});

	return (
		<Group gap="xs" wrap="nowrap" data-testid="development-task-switcher">
			<Text size="sm" c="dimmed">
				{t("pages.development.tasks.label", "Task")}
			</Text>
			<Select
				size="xs"
				style={{ flex: 1, minWidth: 0 }}
				data={options}
				value={selectedTaskId}
				allowDeselect={false}
				onChange={(value) => (value ? onSelect(value) : undefined)}
				aria-label={t("pages.development.tasks.label", "Task")}
				data-testid="development-task-select"
			/>
			<Text size="xs" c="dimmed" data-testid="development-task-count">
				{t("pages.development.tasks.count", "{{count}} in this project", { count: tasks.length })}
			</Text>
		</Group>
	);
}
