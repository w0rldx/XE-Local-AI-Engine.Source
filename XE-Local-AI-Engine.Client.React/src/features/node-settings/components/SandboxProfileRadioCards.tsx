import { Badge, Group, List, Radio, Stack, Text, Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

export type SandboxSecurityProfile = "low" | "high";

interface Props {
	// The chosen profile, or null while nothing is chosen (the first-run chooser preselects neither).
	readonly value: SandboxSecurityProfile | null;
	readonly onChange: (value: SandboxSecurityProfile) => void;
	// Which card carries the "Recommended" badge; null shows no badge (the capability read failed or is still loading).
	readonly recommended: SandboxSecurityProfile | null;
	// The workloads `high` would refuse on this host, listed under the High card. Empty or absent lists nothing.
	readonly highRefusals: readonly string[] | undefined;
	readonly testIdPrefix: string;
}

// The two profiles as one radio question: the first-run chooser and the Node settings card both render this, so the
// trade-off copy an operator decides on is the copy they later review.
export function SandboxProfileRadioCards({ value, onChange, recommended, highRefusals, testIdPrefix }: Props) {
	const { t } = useTranslation();
	const badge = (
		<Badge variant="light" data-testid={`${testIdPrefix}-recommended`}>
			{t("pages.sandboxProfile.recommended", "Recommended for this host")}
		</Badge>
	);

	return (
		<Radio.Group
			value={value ?? ""}
			onChange={(next) => onChange(next === "high" ? "high" : "low")}
			aria-label={t("pages.sandboxProfile.setupTitle", "How strict should the sandbox be?")}
		>
			<Stack gap="md">
				<Radio.Card value="low" radius="lg" p="xl" data-testid={`${testIdPrefix}-low-card`}>
					<Group align="flex-start" wrap="nowrap" gap="md">
						<Radio.Indicator />
						<Stack gap="xs">
							<Group gap="sm" align="center">
								<Title order={2} size="h3">
									{t("pages.sandboxProfile.low.title", "Low")}
								</Title>
								{recommended === "low" ? badge : null}
							</Group>
							<Text>
								{t(
									"pages.sandboxProfile.low.body",
									"Every workload runs with the boundaries this host can give it. A workload that asks for a boundary the host lacks still runs, without it. No extra overhead, and nothing is refused beyond the safety floors every profile keeps.",
								)}
							</Text>
						</Stack>
					</Group>
				</Radio.Card>

				<Radio.Card value="high" radius="lg" p="xl" data-testid={`${testIdPrefix}-high-card`}>
					<Group align="flex-start" wrap="nowrap" gap="md">
						<Radio.Indicator />
						<Stack gap="xs">
							<Group gap="sm" align="center">
								<Title order={2} size="h3">
									{t("pages.sandboxProfile.high.title", "High")}
								</Title>
								{recommended === "high" ? badge : null}
							</Group>
							<Text>
								{t(
									"pages.sandboxProfile.high.body",
									"Every boundary a workload asks for becomes a requirement: filesystem isolation, resource limits and denied network access. A workload this host cannot isolate that way refuses to start instead of running with less. Resource limits add a little start-up overhead, and some workloads may not run on this host.",
								)}
							</Text>
							{highRefusals !== undefined && highRefusals.length > 0 ? (
								<Stack gap={4} data-testid={`${testIdPrefix}-refusals`}>
									<Text size="sm" fw={500}>
										{t("pages.sandboxProfile.refusalsTitle", "These workloads will refuse on this host:")}
									</Text>
									<List size="sm">
										{highRefusals.map((role) => (
											<List.Item key={role}>{role}</List.Item>
										))}
									</List>
								</Stack>
							) : null}
						</Stack>
					</Group>
				</Radio.Card>
			</Stack>
		</Radio.Group>
	);
}
