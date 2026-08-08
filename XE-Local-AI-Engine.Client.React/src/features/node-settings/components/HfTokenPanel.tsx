import { Badge, Button, Card, Group, PasswordInput, Stack, Text, Title } from "@mantine/core";
import { IconKey, IconTrash } from "@tabler/icons-react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";

interface HfTokenPanelProps {
	// Whether a token is currently configured. A boolean ONLY — the token value is write-only and never returned.
	hasToken: boolean;
	isLoading: boolean;
	// The masked input draft, lifted to the page store so it is never derived from server state.
	tokenDraft: string;
	onTokenDraftChange: (value: string) => void;
	onSave: () => void;
	onClear: () => void;
	isSaving: boolean;
}

// HF token entry panel. The token is a SECRET: this panel renders a masked (PasswordInput) draft only, surfaces a
// boolean "configured" / "none" status (never the value), and offers save + clear actions. The token value is never
// read back from the server, never echoed, and never placed in the draft from server state.
export function HfTokenPanel({
	hasToken,
	isLoading,
	tokenDraft,
	onTokenDraftChange,
	onSave,
	onClear,
	isSaving,
}: HfTokenPanelProps) {
	const { t } = useTranslation();

	const handleSubmit = (event: FormEvent): void => {
		event.preventDefault();
		onSave();
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="model-fit-hf-token-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Group gap="xs" align="center">
						<IconKey size={20} />
						<Title order={4}>{t("pages.nodeSettings.hfToken.title", "Hugging Face token")}</Title>
					</Group>
					{!isLoading ? (
						hasToken ? (
							<Badge color="green" variant="light" data-testid="model-fit-hf-token-status">
								{t("pages.nodeSettings.hfToken.configured", "Token configured")}
							</Badge>
						) : (
							<Badge color="gray" variant="light" data-testid="model-fit-hf-token-status">
								{t("pages.nodeSettings.hfToken.none", "No token")}
							</Badge>
						)
					) : null}
				</Group>

				<Text size="sm" c="dimmed">
					{t(
						"pages.nodeSettings.hfToken.description",
						"A Hugging Face access token is required to download gated GGUF repositories. The token is stored encrypted and never displayed.",
					)}
				</Text>

				<form onSubmit={handleSubmit}>
					<Stack gap="sm">
						<PasswordInput
							label={t("pages.nodeSettings.hfToken.inputLabel", "Access token")}
							placeholder={t("pages.nodeSettings.hfToken.placeholder", "hf_…")}
							value={tokenDraft}
							onChange={(event) => onTokenDraftChange(event.currentTarget.value)}
							autoComplete="off"
							data-testid="model-fit-hf-token-input"
						/>
						<Group gap="sm">
							<Button
								type="submit"
								loading={isSaving}
								disabled={tokenDraft.trim().length === 0}
								data-testid="model-fit-hf-token-save"
							>
								{t("pages.nodeSettings.hfToken.save", "Save token")}
							</Button>
							<Button
								variant="default"
								color="red"
								leftSection={<IconTrash size={16} />}
								loading={isSaving}
								disabled={!hasToken}
								onClick={onClear}
								data-testid="model-fit-hf-token-clear"
							>
								{t("pages.nodeSettings.hfToken.clear", "Clear token")}
							</Button>
						</Group>
					</Stack>
				</form>
			</Stack>
		</Card>
	);
}
