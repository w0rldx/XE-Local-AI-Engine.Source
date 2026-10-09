import {
	Anchor,
	Box,
	Button,
	Card,
	Container,
	Group,
	List,
	Loader,
	PasswordInput,
	Stack,
	Text,
	TextInput,
	Title,
} from "@mantine/core";
import { IconLockOpen } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { isAxiosError } from "axios";
import type { FormEvent } from "react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { getNodeAuthStatus, unlockNodeVault, unlockNodeVaultWithRecovery } from "@/core/auth/api/NodeAuthApi";
import type { NodeAuthErrorResponse } from "@/core/auth/models/NodeAuthModels";
import { RecoveryCodeReveal } from "@/core/auth/components/RecoveryCodeReveal";
import { unmetPasswordRules } from "@/core/auth/models/PasswordPolicy";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { StandaloneScreenControls } from "@/core/ui/components/StandaloneScreenControls/StandaloneScreenControls";

type Translate = (key: string, options?: Record<string, unknown>) => string;

interface RecoveryFormValues {
	recoveryCode: string;
	newPassword: string;
	confirmPassword: string;
}

const handoverBudgetMs = 60_000;
const handoverFirstDelayMs = 500;
const handoverMaxDelayMs = 2_000;
const handoverRequestTimeoutMs = 5_000;

const passwordRuleKeys = [
	"auth.setup.passwordRuleLength",
	"auth.setup.passwordRuleUppercase",
	"auth.setup.passwordRuleLowercase",
	"auth.setup.passwordRuleDigit",
	"auth.setup.passwordRuleSymbol",
] as const;

// After a successful unlock the pre-host stops and the real host re-binds the same origin; until it does, requests fail with a
// connection error or 503, and a still-draining pre-host may answer "locked" once more. All three mean "keep waiting".
async function waitForUnlockedHost(isCancelled: () => boolean): Promise<boolean> {
	let delay = handoverFirstDelayMs;
	for (let waited = 0; waited < handoverBudgetMs; waited += delay, delay = Math.min(delay * 2, handoverMaxDelayMs)) {
		// biome-ignore lint/performance/noAwaitInLoops: a backoff poll is sequential by definition.
		await new Promise((resolve) => setTimeout(resolve, delay));
		if (isCancelled()) {
			return false;
		}

		try {
			const status = await getNodeAuthStatus({ timeout: handoverRequestTimeoutMs });
			if (status.vault !== "locked") {
				return true;
			}
		} catch {
			// The hand-over window; the next attempt decides.
		}
	}

	return false;
}

function getErrorMessage(error: unknown, recovery: boolean, t: Translate): string {
	if (isAxiosError<NodeAuthErrorResponse>(error)) {
		const status = error.response?.status;
		if (status === 429) {
			return t("auth.vault.errorRateLimited");
		}

		if (status === 401) {
			return t(recovery ? "auth.vault.errorInvalidRecoveryCode" : "auth.vault.errorIncorrectPassword");
		}

		const errors = error.response?.data?.errors;
		if (status === 400 && errors && errors.length > 0) {
			return errors.join(" ");
		}

		if (!error.response) {
			return t("auth.vault.errorNodeUnreachable");
		}
	}

	return t("auth.vault.errorGeneric");
}

function validateRecovery(values: RecoveryFormValues, t: Translate): Partial<Record<keyof RecoveryFormValues, string>> {
	const errors: Partial<Record<keyof RecoveryFormValues, string>> = {};
	if (values.recoveryCode.trim().length === 0) {
		errors.recoveryCode = t("auth.vault.validationRecoveryCodeRequired");
	}

	const unmet = unmetPasswordRules(values.newPassword);
	if (values.newPassword.length === 0) {
		errors.newPassword = t("auth.setup.validationPasswordRequired");
	} else if (unmet.length > 0) {
		errors.newPassword = t("auth.setup.validationPasswordWeak", { unmet: unmet.join(", ") });
	}

	if (values.confirmPassword !== values.newPassword) {
		errors.confirmPassword = t("auth.setup.validationPasswordsNoMatch");
	}

	return errors;
}

export function VaultUnlock() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const [useRecovery, setUseRecovery] = useState(false);
	const [password, setPassword] = useState("");
	const [recovery, setRecovery] = useState<RecoveryFormValues>({ recoveryCode: "", newPassword: "", confirmPassword: "" });
	const [touched, setTouched] = useState<Partial<Record<keyof RecoveryFormValues, boolean>>>({});
	const [error, setError] = useState<string | undefined>();
	const [submitting, setSubmitting] = useState(false);
	const [handover, setHandover] = useState<"idle" | "starting" | "failed">("idle");
	// The rotated recovery code from a recovery unlock: held here while the real host takes over, because the pre-host
	// answers before that host commits the reset, and shown once only after the hand-over succeeded.
	const pendingRecoveryCode = useRef<string | undefined>(undefined);
	const [newRecoveryCode, setNewRecoveryCode] = useState<string | undefined>();
	const unmounted = useRef(false);
	const recoveryErrors = useMemo(() => validateRecovery(recovery, t), [recovery, t]);
	const canSubmit = useRecovery ? Object.keys(recoveryErrors).length === 0 : password.length > 0;

	useEffect(() => {
		unmounted.current = false;
		return () => {
			unmounted.current = true;
		};
	}, []);

	const visibleError = (field: keyof RecoveryFormValues): string | undefined =>
		touched[field] === true ? recoveryErrors[field] : undefined;
	const updateRecovery = (field: keyof RecoveryFormValues, value: string): void => {
		setRecovery((current) => ({ ...current, [field]: value }));
		setTouched((current) => (current[field] === true ? current : { ...current, [field]: true }));
	};

	const awaitHandover = async (): Promise<void> => {
		setHandover("starting");
		const unlocked = await waitForUnlockedHost(() => unmounted.current);
		if (unmounted.current) {
			return;
		}

		if (unlocked) {
			if (pendingRecoveryCode.current) {
				setNewRecoveryCode(pendingRecoveryCode.current);
				pendingRecoveryCode.current = undefined;
				setHandover("idle");
				return;
			}

			// Unlocking issues no session; the layout guard sends the operator on to the normal sign-in.
			await navigate({ to: "/" });
			return;
		}

		setHandover("failed");
	};

	const handleSubmit = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
		event.preventDefault();
		setError(undefined);
		if (!canSubmit) {
			return;
		}

		setSubmitting(true);
		let rotatedCode: string | undefined;
		try {
			if (useRecovery) {
				const response = await unlockNodeVaultWithRecovery({
					recoveryCode: recovery.recoveryCode.trim(),
					newPassword: recovery.newPassword,
				});
				rotatedCode = response.recoveryCode;
			} else {
				await unlockNodeVault({ password });
			}
		} catch (submitError) {
			setError(getErrorMessage(submitError, useRecovery, t));
			setSubmitting(false);
			return;
		}

		setSubmitting(false);
		setPassword("");
		setRecovery({ recoveryCode: "", newPassword: "", confirmPassword: "" });
		pendingRecoveryCode.current = rotatedCode;
		await awaitHandover();
	};

	const renderBody = () => {
		if (newRecoveryCode) {
			return (
				<Stack gap="md">
					<Text size="sm">{t("auth.vault.recoveryRotated")}</Text>
					<RecoveryCodeReveal
						recoveryCode={newRecoveryCode}
						onContinue={() => {
							setNewRecoveryCode(undefined);
							// Unlocking issues no session; the layout guard sends the operator on to the normal sign-in.
							navigate({ to: "/" }).catch(() => undefined);
						}}
					/>
				</Stack>
			);
		}

		if (handover === "starting") {
			return (
				<Group justify="center" gap="sm" role="status">
					<Loader size="sm" />
					<Text>{t("auth.vault.startingEngine")}</Text>
				</Group>
			);
		}

		if (handover === "failed") {
			return (
				<InlineErrorAlert message={t("auth.vault.errorHandoverTimeout")}>
					<Button
						variant="light"
						onClick={() => {
							awaitHandover().catch(() => undefined);
						}}
					>
						{t("auth.vault.tryAgainButton")}
					</Button>
				</InlineErrorAlert>
			);
		}

		return (
			<>
				{error ? <InlineErrorAlert message={error} /> : null}
				<form onSubmit={handleSubmit}>
					<Stack gap="md">
						{useRecovery ? (
							<>
								<TextInput
									label={t("auth.vault.recoveryCodeLabel")}
									autoComplete="off"
									spellCheck={false}
									required={true}
									value={recovery.recoveryCode}
									onChange={(event) => {
										updateRecovery("recoveryCode", event.currentTarget.value);
									}}
									error={visibleError("recoveryCode")}
								/>
								<Stack gap={4}>
									<PasswordInput
										label={t("auth.vault.newPasswordLabel")}
										autoComplete="new-password"
										required={true}
										value={recovery.newPassword}
										onChange={(event) => {
											updateRecovery("newPassword", event.currentTarget.value);
										}}
										error={visibleError("newPassword")}
									/>
									<List size="xs" c="dimmed" spacing={0} withPadding={true}>
										{passwordRuleKeys.map((key) => (
											<List.Item key={key}>{t(key)}</List.Item>
										))}
									</List>
								</Stack>
								<PasswordInput
									label={t("auth.vault.confirmPasswordLabel")}
									autoComplete="new-password"
									required={true}
									value={recovery.confirmPassword}
									onChange={(event) => {
										updateRecovery("confirmPassword", event.currentTarget.value);
									}}
									error={visibleError("confirmPassword")}
								/>
							</>
						) : (
							<PasswordInput
								label={t("auth.vault.passwordLabel")}
								autoComplete="current-password"
								required={true}
								value={password}
								onChange={(event) => {
									setPassword(event.currentTarget.value);
								}}
							/>
						)}
						<Button type="submit" loading={submitting} disabled={!canSubmit} fullWidth={true}>
							{t(useRecovery ? "auth.vault.recoveryUnlockButton" : "auth.vault.unlockButton")}
						</Button>
						<Anchor
							component="button"
							type="button"
							size="sm"
							ta="center"
							onClick={() => {
								setUseRecovery((current) => !current);
								setError(undefined);
							}}
						>
							{t(useRecovery ? "auth.vault.usePasswordToggle" : "auth.vault.useRecoveryToggle")}
						</Anchor>
					</Stack>
				</form>
			</>
		);
	};

	return (
		<Box pos="relative">
			<StandaloneScreenControls />
			<Container size="xs" py="xl" className="min-h-dvh flex items-center">
				<Card withBorder={true} radius="lg" p="xl" className="w-full">
					<Stack gap="lg">
						<Stack gap={4} align="center" ta="center">
							<IconLockOpen size={36} aria-hidden="true" />
							<Title order={1}>{t("auth.vault.unlockTitle")}</Title>
							<Text c="dimmed">{t(useRecovery ? "auth.vault.recoverySubtitle" : "auth.vault.unlockSubtitle")}</Text>
						</Stack>
						{renderBody()}
					</Stack>
				</Card>
			</Container>
		</Box>
	);
}
