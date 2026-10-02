import { Alert, Button, Checkbox, Code, CopyButton, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconCopy } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

interface RecoveryCodeRevealProps {
	recoveryCode: string;
	onContinue: () => void;
}

// Show-once reveal, the IntegrationKeyRevealPanel pattern: the node keeps only a wrap made from this code, so it lives
// in the caller's component state and nowhere else. The checkbox makes the operator acknowledge it before moving on,
// because a lost code plus a forgotten password means the node's data is gone.
export function RecoveryCodeReveal({ recoveryCode, onContinue }: RecoveryCodeRevealProps) {
	const { t } = useTranslation();
	const [saved, setSaved] = useState(false);

	return (
		<Stack gap="md">
			<Alert color="yellow" variant="light" icon={<IconAlertTriangle size={18} />} title={t("auth.vault.recoveryRevealTitle")}>
				<Stack gap="xs">
					<Text size="sm">{t("auth.vault.recoveryRevealWarning")}</Text>
					<Group gap="xs" wrap="nowrap">
						<Code data-testid="recovery-code-value" style={{ flex: 1, overflowX: "auto" }}>
							{recoveryCode}
						</Code>
						<CopyButton value={recoveryCode}>
							{({ copied, copy }) => (
								<Button
									variant="default"
									size="xs"
									leftSection={copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
									onClick={copy}
								>
									{copied ? t("common.copied") : t("common.copy")}
								</Button>
							)}
						</CopyButton>
					</Group>
				</Stack>
			</Alert>
			<Checkbox
				label={t("auth.vault.recoverySavedLabel")}
				checked={saved}
				onChange={(event) => {
					setSaved(event.currentTarget.checked);
				}}
			/>
			<Button disabled={!saved} onClick={onContinue} fullWidth={true}>
				{t("auth.vault.continueButton")}
			</Button>
		</Stack>
	);
}
