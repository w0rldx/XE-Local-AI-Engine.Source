import { Alert, Badge, Button, Card, Group, Loader, Select, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconBinary, IconDownload, IconSearch } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import {
	type LlamaCppVariant,
	type LlamaCppVersion,
	llamaCppVariants,
} from "@/features/model-fit/models/ModelFitModels";

interface LlamaCppVersionPanelProps {
	version: LlamaCppVersion | undefined;
	isLoading: boolean;
	error: unknown;
	// Whether the operator has triggered the version probe at least once. Until then the panel shows an idle state and
	// never reads the version — the GET can trigger the first prebuilt-binary download, so it must be operator-initiated.
	hasChecked: boolean;
	// Operator-initiated probe of the resolved llama.cpp version (and, backend-side, the first prebuilt download if absent).
	onCheck: () => void;
	onEnsure: (variant: LlamaCppVariant) => void;
	isEnsuring: boolean;
}

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

function isLlamaCppVariant(value: string): value is LlamaCppVariant {
	return (llamaCppVariants as readonly string[]).includes(value);
}

// llama.cpp binary panel: shows the resolved version + variant (+ a pinned-fallback badge) and lets the operator
// select a variant to ensure/download. Server state is owned by the page's useLlamaCppVersion query + ensure mutation;
// the selected-variant draft is local component state (it never derives from server state).
export function LlamaCppVersionPanel({
	version,
	isLoading,
	error,
	hasChecked,
	onCheck,
	onEnsure,
	isEnsuring,
}: LlamaCppVersionPanelProps) {
	const { t } = useTranslation();
	const [selectedVariant, setSelectedVariant] = useState<LlamaCppVariant>("cpu");

	const variantData = llamaCppVariants.map((value) => ({
		value,
		label: t(`pages.modelFit.llamaCpp.variants.${value}`, value),
	}));

	const handleVariantChange = (value: string | null): void => {
		if (value !== null && isLlamaCppVariant(value)) {
			setSelectedVariant(value);
		}
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="model-fit-llamacpp-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Group gap="xs" align="center">
						<IconBinary size={20} />
						<Title order={4}>{t("pages.modelFit.llamaCpp.title", "llama.cpp runtime")}</Title>
					</Group>
					<Button
						variant="default"
						leftSection={<IconSearch size={16} />}
						loading={isLoading}
						onClick={onCheck}
						data-testid="model-fit-llamacpp-check-button"
					>
						{t("pages.modelFit.llamaCpp.check", "Check version")}
					</Button>
				</Group>

				{!hasChecked && !isLoading ? (
					<Text c="dimmed" size="sm" data-testid="model-fit-llamacpp-idle">
						{t(
							"pages.modelFit.llamaCpp.idle",
							"Not checked yet. Checking resolves the llama.cpp binary and may download it on first use.",
						)}
					</Text>
				) : null}

				{isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.modelFit.llamaCpp.loading", "Resolving llama.cpp binary…")}</Text>
					</Group>
				) : null}

				{error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="model-fit-llamacpp-error">
						{errorMessage(error, t("pages.modelFit.llamaCpp.error", "Could not resolve the llama.cpp binary."))}
					</Alert>
				) : null}

				{hasChecked && version && !isLoading && !error ? (
					<Group gap="xl" align="flex-end">
						<Stack gap={0}>
							<Text size="xs" c="dimmed">
								{t("pages.modelFit.llamaCpp.version", "Version")}
							</Text>
							<Group gap="xs">
								<Text size="sm" fw={500} ff="monospace" data-testid="model-fit-llamacpp-version">
									{version.version || "—"}
								</Text>
								{version.isPinnedFallback ? (
									<Badge color="yellow" variant="light" data-testid="model-fit-llamacpp-pinned-badge">
										{t("pages.modelFit.llamaCpp.pinned", "Pinned fallback")}
									</Badge>
								) : null}
							</Group>
						</Stack>
						<Stack gap={0}>
							<Text size="xs" c="dimmed">
								{t("pages.modelFit.llamaCpp.variant", "Variant")}
							</Text>
							<Badge variant="outline" data-testid="model-fit-llamacpp-variant">
								{t(`pages.modelFit.llamaCpp.variants.${version.variant}`, version.variant)}
							</Badge>
						</Stack>
					</Group>
				) : null}

				<Group gap="sm" align="flex-end">
					<Select
						label={t("pages.modelFit.llamaCpp.selectVariant", "Variant")}
						data={variantData}
						value={selectedVariant}
						onChange={handleVariantChange}
						allowDeselect={false}
						data-testid="model-fit-llamacpp-variant-select"
					/>
					<Button
						leftSection={<IconDownload size={16} />}
						loading={isEnsuring}
						onClick={() => onEnsure(selectedVariant)}
						data-testid="model-fit-llamacpp-ensure-button"
					>
						{t("pages.modelFit.llamaCpp.ensure", "Ensure / select")}
					</Button>
				</Group>
			</Stack>
		</Card>
	);
}
