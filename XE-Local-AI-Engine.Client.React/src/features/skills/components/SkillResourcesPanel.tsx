import { Alert, Button, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconEye, IconEyeOff } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { CodeBlock } from "@/core/ui/components/CodeBlock/CodeBlock";
import { useSkillResourceContent, useSkillResources } from "@/features/skills/queries/useSkillImport";

interface SkillResourcesPanelProps {
	skillId: string;
}

/**
 * Files bundled with a stored skill (imported skills only — a locally authored one has none). Content is fetched one
 * resource at a time, on demand: a bundled file can be several MiB, and the operator only needs the one they opened.
 *
 * Resources are as untrusted as the body — a skill's instructions can tell the agent to read any of them — so viewing
 * a resource's actual content has to stay one click away, not a promise made in the import dialog and then withheld.
 */
export function SkillResourcesPanel({ skillId }: SkillResourcesPanelProps) {
	const { t } = useTranslation();
	const [openResource, setOpenResource] = useState<string | null>(null);

	const resourcesQuery = useSkillResources(skillId);
	const contentQuery = useSkillResourceContent(skillId, openResource);

	if (resourcesQuery.isLoading) {
		return <Loader size="sm" data-testid="skill-resources-loading" />;
	}

	if (resourcesQuery.error) {
		return (
			<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="skill-resources-error">
				{apiErrorMessage(resourcesQuery.error, t("pages.skills.resources.error", "Could not load the bundled resources."))}
			</Alert>
		);
	}

	const resources = resourcesQuery.data ?? [];
	if (resources.length === 0) {
		return null;
	}

	return (
		<Stack gap="xs" data-testid="skill-resources-panel">
			<Text size="sm" fw={600}>
				{t("pages.skills.resources.title", "Bundled resources ({{count}})", { count: resources.length })}
			</Text>
			{resources.map((resource) => {
				const isOpen = openResource === resource.name;
				return (
					<Stack gap={4} key={resource.name} data-testid={`skill-resource-${resource.name}`}>
						<Group gap="xs" wrap="nowrap" justify="space-between">
							<Text size="xs" ff="monospace" style={{ minWidth: 0, overflow: "hidden", textOverflow: "ellipsis" }}>
								{resource.name}
							</Text>
							<Group gap="xs" wrap="nowrap">
								<Text size="xs" c="dimmed">
									{`${resource.mediaType} · ${resource.sizeBytes.toLocaleString()} B`}
								</Text>
								<Button
									size="compact-xs"
									variant="subtle"
									leftSection={isOpen ? <IconEyeOff size={12} /> : <IconEye size={12} />}
									onClick={() => setOpenResource(isOpen ? null : resource.name)}
									data-testid={`skill-resource-view-${resource.name}`}
								>
									{isOpen ? t("common.hide", "Hide") : t("common.view", "View")}
								</Button>
							</Group>
						</Group>
						{isOpen ? (
							contentQuery.isLoading ? (
								<Loader size="xs" />
							) : contentQuery.error ? (
								<Alert color="red" icon={<IconAlertTriangle size={16} />}>
									{apiErrorMessage(contentQuery.error, t("pages.skills.resources.error", "Could not load the bundled resources."))}
								</Alert>
							) : (
								<div data-testid={`skill-resource-content-${resource.name}`}>
									<CodeBlock language="markdown" code={contentQuery.data?.content ?? ""} />
								</div>
							)
						) : null}
					</Stack>
				);
			})}
		</Stack>
	);
}
