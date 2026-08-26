import { Accordion, Alert, Group, Loader, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import type { ComponentProps } from "react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { DevelopmentProjectForm } from "@/features/development/components/DevelopmentProjectForm";

interface DevelopmentProjectSetupProps {
	readonly form: ComponentProps<typeof DevelopmentProjectForm>;
	readonly projects: {
		readonly count: number;
		readonly error?: string;
		readonly loading: boolean;
	};
}

export function DevelopmentProjectSetup({ form, projects }: DevelopmentProjectSetupProps) {
	const { t } = useTranslation();
	return (
		<>
			<Accordion defaultValue={projects.count === 0 ? "create" : null} variant="contained">
				<Accordion.Item value="create">
					<Accordion.Control>{t("pages.development.newProject", "New Development project")}</Accordion.Control>
					<Accordion.Panel>
						<DevelopmentProjectForm {...form} />
					</Accordion.Panel>
				</Accordion.Item>
			</Accordion>
			{projects.loading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.development.loading.projects", "Loading Development projects")}</Text>
				</Group>
			) : null}
			{projects.error ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />}>
					{projects.error}
				</Alert>
			) : null}
			{projects.count === 0 && !projects.loading ? (
				<SectionCard data-testid="development-empty-state" gap="xs">
					<Text fw={600}>{t("pages.development.empty.title", "No Development projects yet")}</Text>
					<Text c="dimmed">
						{t("pages.development.empty.body", "Create the initial project and task above. This workflow never enters Chat.")}
					</Text>
				</SectionCard>
			) : null}
		</>
	);
}
