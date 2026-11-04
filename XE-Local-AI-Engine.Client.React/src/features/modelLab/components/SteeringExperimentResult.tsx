import { Alert, Badge, List, Paper, SimpleGrid, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type {
	XeLocalAiEngineClientServicesModelLabModelLabTraceResult as ModelLabResult,
	XeLocalAiEngineClientServicesModelLabModelLabSteeringArm as SteeringArm,
} from "@/core/api/generated/types.gen";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { layerKindLabel, savedLayerKind } from "@/features/modelLab/models/ModelLabSteering";

const armOrder = ["baseline", "vector", "random"];

// A saved steering run: what the numbers do not say, what was derived, and each prompt's three outputs side by side.
export function SteeringExperimentResult({ result, running }: { result: ModelLabResult | null | undefined; running: boolean }) {
	const { t } = useTranslation();
	const steering = result?.steering;
	const arms = armOrder.map((name) => steering?.arms?.find((arm) => arm.name === name));
	// Positions are the rows' identity: the same prompt may be listed twice, and a saved list never reorders.
	const rows =
		arms.find((arm) => arm !== undefined)?.outputs.map((output, position) => ({ prompt: output.prompt, position })) ?? [];
	const armLabels: Record<string, string> = {
		baseline: t("pages.modelLab.steer.armBaseline", "Baseline"),
		vector: t("pages.modelLab.steer.armVector", "Vector"),
		random: t("pages.modelLab.steer.armRandom", "Random control"),
	};
	const ran = arms.find((arm): arm is SteeringArm => arm !== undefined);

	return (
		<Stack gap="sm" data-testid="model-lab-steering-result">
			<Alert color="blue" data-testid="model-lab-steering-caveats">
				<List size="sm" spacing={4}>
					<List.Item>{t("pages.modelLab.steer.caveatChange", "A changed output is not a finding on its own.")}</List.Item>
					<List.Item>{t("pages.modelLab.steer.caveatRandom", "A random vector also changes output.")}</List.Item>
					<List.Item>
						{t("pages.modelLab.steer.caveatSource", "The vector came from the BF16 checkpoint, the outputs from the GGUF.")}
					</List.Item>
					<List.Item>{t("pages.modelLab.steer.caveatArms", "These arms compare only with each other.")}</List.Item>
					<List.Item>
						{t("pages.modelLab.steer.caveatStrength", "Equal relative strength in the checkpoint and the GGUF is an assumption.")}
					</List.Item>
					<List.Item>
						{t(
							"pages.modelLab.steer.caveatPairing",
							"The pairing is by shape only and yours to get right: another model of that shape takes this vector with no error.",
						)}
					</List.Item>
				</List>
			</Alert>
			{steering && (
				<Text size="xs" c="dimmed" data-testid="model-lab-steering-facts">
					{t(
						"pages.modelLab.steer.facts",
						"Layer {{layer}} ({{kind}}), scale {{scale}}, vector length {{norm}}, vector {{hash}}. Derived in {{seconds}} s, peak {{vram}} on {{device}}.",
						{
							layer: steering.layer,
							kind: layerKindLabel(t, savedLayerKind(steering.layerKind)),
							scale: steering.scale,
							norm: steering.vectorNorm?.toFixed(2) ?? "?",
							hash: steering.vectorSha256?.slice(0, 12) ?? "?",
							seconds: result?.seconds,
							vram: humanizeBytes(result?.peakVramBytes ?? 0),
							device: result?.device ?? "?",
						},
					)}{" "}
					{ran &&
						t("pages.modelLab.steer.runtime", "Runtime {{runtime}}, {{build}}.", {
							runtime: ran.runtime ?? "?",
							build: ran.build ?? "?",
						})}
				</Text>
			)}
			{rows.map(({ prompt, position }) => (
				<Stack key={position} gap="xs">
					<Text size="sm" fw={600}>
						{prompt}
					</Text>
					<SimpleGrid cols={{ base: 1, md: 3 }} spacing="xs">
						{armOrder.map((name, armIndex) => {
							const output = arms[armIndex]?.outputs[position];
							return (
								<Paper key={name} withBorder={true} p="xs" data-testid={`model-lab-arm-${name}`}>
									<Stack gap={4}>
										<Badge variant="light" color={name === "vector" ? "blue" : "gray"} w="fit-content">
											{armLabels[name]}
										</Badge>
										{output ? (
											<Text size="sm" style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>
												{output.text}
											</Text>
										) : (
											<Text size="sm" c="dimmed">
												{running
													? t("pages.modelLab.steer.armPending", "This arm has not finished yet.")
													: t("pages.modelLab.steer.armMissing", "This arm did not finish.")}
											</Text>
										)}
									</Stack>
								</Paper>
							);
						})}
					</SimpleGrid>
				</Stack>
			))}
		</Stack>
	);
}
