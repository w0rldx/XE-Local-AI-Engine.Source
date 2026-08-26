import { ActionIcon, Button, Card, Flex, Group, NumberInput, SimpleGrid, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconCoin, IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { newUsageRateRow, type UsageRateRow } from "@/features/node-settings/models/NodeSettingsFieldsModel";

interface Props {
	readonly usageRates: UsageRateRow[];
	readonly error?: string;
	readonly onChange: (rates: UsageRateRow[]) => void;
}
export function NodeSettingsUsageRatesCard({ usageRates, error: usageRatesError, onChange }: Props) {
	const { t } = useTranslation();
	const form = { usageRates };
	const addRateRow = (): void => onChange([...usageRates, newUsageRateRow()]);
	const updateRateRow = (id: string, patch: Partial<Omit<UsageRateRow, "id">>): void =>
		onChange(usageRates.map((row) => (row.id === id ? { ...row, ...patch } : row)));
	const removeRateRow = (id: string): void => onChange(usageRates.filter((row) => row.id !== id));
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-usage-rates-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={4}>{t("pages.nodeSettings.fields.usageRates.title", "Usage cost rates")}</Title>
					<IconCoin size={20} />
				</Group>
				<Text size="sm" c="dimmed">
					{t(
						"pages.nodeSettings.fields.usageRates.description",
						"Approximate per-model cost rates (USD per 1M tokens) used to estimate cost on the Usage dashboard. Local and unpriced models are treated as free. These are estimates in your operator currency (USD), not a bill.",
					)}
				</Text>
				{form.usageRates.length === 0 ? (
					<Text size="sm" c="dimmed" data-testid="node-settings-usage-rates-empty">
						{t("pages.nodeSettings.fields.usageRates.empty", "No rates configured. Add a rate to estimate cost for a model.")}
					</Text>
				) : (
					<Stack gap="xs">
						<Group gap="sm" wrap="nowrap" visibleFrom="sm">
							<Text size="xs" fw={600} c="dimmed" style={{ flex: 1 }}>
								{t("pages.nodeSettings.fields.usageRates.columns.model", "Model name")}
							</Text>
							<Text size="xs" fw={600} c="dimmed" style={{ width: 140 }}>
								{t("pages.nodeSettings.fields.usageRates.columns.input", "Input $/1M")}
							</Text>
							<Text size="xs" fw={600} c="dimmed" style={{ width: 140 }}>
								{t("pages.nodeSettings.fields.usageRates.columns.output", "Output $/1M")}
							</Text>
							<div style={{ width: 36 }} />
						</Group>
						{form.usageRates.map((row) => (
							<Group key={row.id} gap="sm" wrap="nowrap" align="flex-start" data-testid="node-settings-usage-rate-row">
								<Flex
									direction={{ base: "column", sm: "row" }}
									gap="sm"
									align={{ base: "stretch", sm: "flex-start" }}
									style={{ flex: "1 1 auto", minWidth: 0 }}
								>
									<TextInput
										aria-label={t("pages.nodeSettings.fields.usageRates.columns.model", "Model name")}
										placeholder={t("pages.nodeSettings.fields.usageRates.modelPlaceholder", "e.g. gpt-5")}
										value={row.modelName}
										onChange={(event) => updateRateRow(row.id, { modelName: event.currentTarget.value })}
										style={{ flex: "1 1 auto", minWidth: 0 }}
										data-testid="node-settings-usage-rate-model"
									/>
									<SimpleGrid cols={2} spacing="sm" style={{ flex: "0 0 auto" }}>
										<Stack gap={4}>
											<Text size="xs" fw={600} c="dimmed" hiddenFrom="sm">
												{t("pages.nodeSettings.fields.usageRates.columns.input", "Input $/1M")}
											</Text>
											<NumberInput
												aria-label={t("pages.nodeSettings.fields.usageRates.columns.input", "Input $/1M")}
												min={0}
												step={0.5}
												decimalScale={4}
												value={row.inputPer1M}
												onChange={(value) => updateRateRow(row.id, { inputPer1M: value })}
												w={{ base: "100%", sm: 140 }}
												data-testid="node-settings-usage-rate-input"
											/>
										</Stack>
										<Stack gap={4}>
											<Text size="xs" fw={600} c="dimmed" hiddenFrom="sm">
												{t("pages.nodeSettings.fields.usageRates.columns.output", "Output $/1M")}
											</Text>
											<NumberInput
												aria-label={t("pages.nodeSettings.fields.usageRates.columns.output", "Output $/1M")}
												min={0}
												step={0.5}
												decimalScale={4}
												value={row.outputPer1M}
												onChange={(value) => updateRateRow(row.id, { outputPer1M: value })}
												w={{ base: "100%", sm: 140 }}
												data-testid="node-settings-usage-rate-output"
											/>
										</Stack>
									</SimpleGrid>
								</Flex>
								<ActionIcon
									variant="subtle"
									color="red"
									aria-label={t("pages.nodeSettings.fields.usageRates.remove", "Remove rate")}
									onClick={() => removeRateRow(row.id)}
									data-testid="node-settings-usage-rate-remove"
								>
									<IconTrash size={16} />
								</ActionIcon>
							</Group>
						))}
					</Stack>
				)}
				{usageRatesError ? (
					<Text size="sm" c="red" data-testid="node-settings-usage-rates-error">
						{usageRatesError}
					</Text>
				) : null}
				<Group>
					<Button
						variant="light"
						size="xs"
						leftSection={<IconPlus size={14} />}
						onClick={addRateRow}
						data-testid="node-settings-usage-rate-add"
					>
						{t("pages.nodeSettings.fields.usageRates.add", "Add rate")}
					</Button>
				</Group>
			</Stack>
		</Card>
	);
}
