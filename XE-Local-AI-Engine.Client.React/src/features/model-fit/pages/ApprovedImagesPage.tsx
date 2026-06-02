import { Alert, Card, Container, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconPhotoShield } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { ApprovedImagesTable } from "@/features/model-fit/components/ApprovedImagesTable";
import { useApprovedImages } from "@/features/model-fit/queries/useModelFit";

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

// READ-ONLY page listing the approved llmfit utility images and why they are used. The pinned image references
// are code/seed-owned and cannot be edited from the browser (plan: read-only image page), so this page renders a
// metadata table only.
export function ApprovedImagesPage() {
	const { t } = useTranslation();
	const imagesQuery = useApprovedImages();
	const images = imagesQuery.data ?? [];

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						{t("pages.modelFit.eyebrow", "Worker Node")}
					</Text>
					<Group gap="xs" align="center">
						<IconPhotoShield size={24} />
						<Title order={2}>{t("pages.modelFit.approvedImages.title", "Approved reference images")}</Title>
					</Group>
					<Text c="dimmed">
						{t(
							"pages.modelFit.approvedImages.subtitle",
							"Approved, digest-pinned llmfit utility images used by the recommendation checker. This list is read-only — image references are managed in code.",
						)}
					</Text>
				</Stack>

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						{imagesQuery.isLoading ? (
							<Group gap="sm">
								<Loader size="sm" />
								<Text c="dimmed">{t("pages.modelFit.approvedImages.loading", "Loading approved images…")}</Text>
							</Group>
						) : null}

						{imagesQuery.error ? (
							<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="model-fit-approved-images-error">
								{errorMessage(
									imagesQuery.error,
									t("pages.modelFit.approvedImages.errors.load", "Could not load approved images."),
								)}
							</Alert>
						) : null}

						{!imagesQuery.isLoading && !imagesQuery.error && images.length === 0 ? (
							<Text c="dimmed" data-testid="model-fit-approved-images-empty">
								{t("pages.modelFit.approvedImages.empty", "No approved images are registered.")}
							</Text>
						) : null}

						{!imagesQuery.isLoading && !imagesQuery.error && images.length > 0 ? (
							<ApprovedImagesTable images={images} />
						) : null}
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
