import { Alert, Button, Group, Loader, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { DevelopmentProject } from "@/features/development/models/DevelopmentModels";

interface DevelopmentProjectListProps {
	readonly error?: string;
	readonly loading: boolean;
	readonly projects: readonly DevelopmentProject[];
	readonly selectedId: string | null;
	readonly onSelect: (id: string | null) => void;
}
export function DevelopmentProjectList({ error, loading, projects, selectedId, onSelect }: DevelopmentProjectListProps) {
	const { t } = useTranslation();
	return (
		<SectionCard gap="xs" title={t("pages.development.projects", "Projects")}>
			{loading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.development.loading.project", "Loading Development project")}</Text>
				</Group>
			) : null}
			{error ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />}>
					{error}
				</Alert>
			) : null}
			{projects.map((project) => (
				<Button
					data-testid={`development-project-${project.id}`}
					justify="space-between"
					key={project.id}
					onClick={() => onSelect(project.id ?? null)}
					variant={project.id === selectedId ? "light" : "subtle"}
				>
					{project.objective ?? t("pages.development.untitledProject", "Untitled project")}
				</Button>
			))}
		</SectionCard>
	);
}
