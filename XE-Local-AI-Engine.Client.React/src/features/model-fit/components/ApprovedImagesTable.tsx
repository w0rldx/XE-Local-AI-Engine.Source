import { Anchor, Badge, Code, Group, Stack, Table, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { formatModelFitTimestamp } from "@/features/model-fit/components/ModelFitFormatters";
import type { ApprovedImage } from "@/features/model-fit/models/ModelFitModels";

interface ApprovedImagesTableProps {
	images: readonly ApprovedImage[];
}

// READ-ONLY presentation of the approved llmfit utility images. The pinned imageReference is code/seed-owned and
// never editable from the browser (plan: "Do not allow arbitrary image editing"), so this table renders metadata
// only — no row actions, no inputs. Each row shows id, display name, purpose badges, the pinned reference, the
// upstream version, an external source link, enabled/deprecated state, last-used / last-successful timestamps,
// and sanitized diagnostics.
export function ApprovedImagesTable({ images }: ApprovedImagesTableProps) {
	const { t } = useTranslation();

	return (
		<Table.ScrollContainer minWidth={1080}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="model-fit-approved-images-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.modelFit.approvedImages.columns.id", "Image ID")}</Table.Th>
						<Table.Th>{t("pages.modelFit.approvedImages.columns.name", "Name")}</Table.Th>
						<Table.Th>{t("pages.modelFit.approvedImages.columns.purpose", "Purpose")}</Table.Th>
						<Table.Th>{t("pages.modelFit.approvedImages.columns.reference", "Pinned reference")}</Table.Th>
						<Table.Th>{t("pages.modelFit.approvedImages.columns.version", "Version")}</Table.Th>
						<Table.Th>{t("pages.modelFit.approvedImages.columns.source", "Source")}</Table.Th>
						<Table.Th>{t("pages.modelFit.approvedImages.columns.state", "State")}</Table.Th>
						<Table.Th>{t("pages.modelFit.approvedImages.columns.lastUsed", "Last used")}</Table.Th>
						<Table.Th>{t("pages.modelFit.approvedImages.columns.lastSuccessful", "Last successful")}</Table.Th>
						<Table.Th>{t("pages.modelFit.approvedImages.columns.diagnostics", "Diagnostics")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{images.map((image) => (
						<Table.Tr
							key={image.approvedImageId}
							data-testid={`model-fit-approved-image-row-${image.approvedImageId}`}
						>
							<Table.Td>
								<Text size="sm" ff="monospace">
									{image.approvedImageId}
								</Text>
							</Table.Td>
							<Table.Td>
								<Stack gap={2}>
									<Text size="sm" fw={500}>
										{image.displayName}
									</Text>
									{image.description ? (
										<Text size="xs" c="dimmed" lineClamp={2}>
											{image.description}
										</Text>
									) : null}
								</Stack>
							</Table.Td>
							<Table.Td>
								<Group gap={4}>
									{image.purpose.length > 0
										? image.purpose.map((purpose) => (
												<Badge key={purpose} variant="light" color="grape">
													{t(`pages.modelFit.approvedImages.purpose.${purpose}`, purpose)}
												</Badge>
											))
										: "—"}
								</Group>
							</Table.Td>
							<Table.Td>
								<Code data-testid={`model-fit-approved-image-reference-${image.approvedImageId}`}>
									{image.imageReference}
								</Code>
							</Table.Td>
							<Table.Td>{image.upstreamVersion ?? "—"}</Table.Td>
							<Table.Td>
								{image.sourceUrl ? (
									<Anchor href={image.sourceUrl} target="_blank" rel="noopener noreferrer" size="sm">
										{t("pages.modelFit.approvedImages.sourceLink", "Open")}
									</Anchor>
								) : (
									"—"
								)}
							</Table.Td>
							<Table.Td>
								{image.deprecatedAtUtc !== null ? (
									<Badge color="orange" variant="light">
										{t("pages.modelFit.approvedImages.state.deprecated", "Deprecated")}
									</Badge>
								) : image.enabled ? (
									<Badge color="green" variant="light">
										{t("pages.modelFit.approvedImages.state.enabled", "Enabled")}
									</Badge>
								) : (
									<Badge color="gray" variant="light">
										{t("pages.modelFit.approvedImages.state.disabled", "Disabled")}
									</Badge>
								)}
							</Table.Td>
							<Table.Td>{formatModelFitTimestamp(image.lastUsedAtUtc)}</Table.Td>
							<Table.Td>{formatModelFitTimestamp(image.lastSuccessfulRunAtUtc)}</Table.Td>
							<Table.Td>
								<Text size="xs" c="dimmed" lineClamp={2}>
									{image.diagnostics ?? "—"}
								</Text>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
