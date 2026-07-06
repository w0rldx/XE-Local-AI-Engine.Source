import type { CSSProperties } from "react";
import { Badge, Box, Card, Code, Group, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconBrain, IconChevronDown, IconDatabase, IconPhoto, IconRobot, IconTool, IconUsersGroup } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

// Z-index sits between app chrome (< 400) and the Joyride overlay (TOUR_Z_INDEX = 1000) so the spotlight and tooltip
// render above the panel but the panel itself occludes the app behind it.
const SHOWCASE_Z_INDEX = 900;

// Fixed centered overlay layout for the showcase panel. The active-dependent backdrop/opacity/visibility are merged in
// at the call site; everything here is static so it never rebuilds on render.
const overlayBaseStyle: CSSProperties = {
	position: "fixed",
	inset: 0,
	zIndex: SHOWCASE_Z_INDEX,
	display: "flex",
	alignItems: "center",
	justifyContent: "center",
	// Inactive: invisible + click-through but still laid out so Joyride can measure the showcase targets.
	// Active: visible; pointerEvents stays none so the underlying app/Joyride spotlight controls interaction.
	pointerEvents: "none",
};

// Static illustrative "fake chat" card shown as a fixed centered overlay during the showcase steps of the tour.
// All sections are purely visual — no interactivity. Each sub-section has a stable `data-tour` attribute that Joyride
// spotlights in the corresponding step. Text is framed as illustrative ("when you use…") so users on hardware that
// cannot run reasoning-capable models are not misled.
//
// The panel is ALWAYS mounted (rendered with `active={false}` outside the showcase steps) so the `data-tour` showcase
// targets are present in the DOM the moment Joyride needs to anchor a showcase step — otherwise Joyride dims the screen
// but can never find the target and the tour dead-ends (no tooltip ever appears). When inactive the panel is visually
// hidden and fully inert, yet keeps layout so Joyride can still measure/anchor its targets.
export function TourShowcasePanel({ active }: { active: boolean }) {
	const { t } = useTranslation();

	return (
		<Box
			data-tour="tour-showcase"
			data-testid="tour-showcase-panel"
			aria-hidden={!active}
			style={{
				...overlayBaseStyle,
				// Backdrop only while active; transparent when inactive so the hidden panel never tints the live app.
				background: active ? "rgba(0,0,0,0.45)" : "transparent",
				opacity: active ? 1 : 0,
				visibility: active ? "visible" : "hidden",
			}}
		>
			<Card
				withBorder={true}
				radius="md"
				p="lg"
				style={{ width: 480, maxWidth: "90vw", pointerEvents: "none" }}
			>
				<Stack gap="md">
					{/* Reasoning-effort control — static disabled segmented pill */}
					<Stack gap={4} data-tour="showcase-reasoning-effort" data-testid="showcase-reasoning-effort">
						<Text size="xs" c="dimmed" fw={600} tt="uppercase">
							{t("onboarding.showcase.reasoningEffort.label")}
						</Text>
						<Group gap={4}>
							{(["low", "medium", "high"] as const).map((level) => (
								<Badge
									key={level}
									variant={level === "medium" ? "filled" : "outline"}
									color={level === "medium" ? "blue" : "gray"}
									style={{ cursor: "default", opacity: level === "medium" ? 1 : 0.55 }}
								>
									{t(`onboarding.showcase.reasoningEffort.${level}`)}
								</Badge>
							))}
						</Group>
						<Text size="xs" c="dimmed">
							{t("onboarding.showcase.reasoningEffort.description")}
						</Text>
					</Stack>

					{/* Reasoning trace — static collapsible "thinking" block */}
					<Stack gap={4} data-tour="showcase-reasoning-trace" data-testid="showcase-reasoning-trace">
						<Group gap={6} align="center">
							<IconBrain size={14} color="var(--mantine-color-violet-6)" />
							<Text size="xs" fw={600} c="violet">
								{t("onboarding.showcase.reasoningTrace.label")}
							</Text>
							<IconChevronDown size={12} style={{ opacity: 0.5 }} />
						</Group>
						<Box
							p="xs"
							style={{
								background: "var(--mantine-color-violet-0)",
								borderRadius: "var(--mantine-radius-sm)",
								borderLeft: "3px solid var(--mantine-color-violet-4)",
							}}
						>
							<Text size="xs" c="dimmed" fs="italic">
								{t("onboarding.showcase.reasoningTrace.sample")}
							</Text>
						</Box>
						<Text size="xs" c="dimmed">
							{t("onboarding.showcase.reasoningTrace.description")}
						</Text>
					</Stack>

					{/* Tool-call render — static name + args + result */}
					<Stack gap={4} data-tour="showcase-tool-call" data-testid="showcase-tool-call">
						<Group gap={6} align="center">
							<IconTool size={14} color="var(--mantine-color-teal-6)" />
							<Text size="xs" fw={600} c="teal">
								{t("onboarding.showcase.toolCall.label")}
							</Text>
						</Group>
						<Code block={true} style={{ fontSize: 12 }}>
							{t("onboarding.showcase.toolCall.sample")}
						</Code>
						<Text size="xs" c="dimmed">
							{t("onboarding.showcase.toolCall.description")}
						</Text>
					</Stack>

					{/* Agent-mode pill — static attribution badge */}
					<Stack gap={4} data-tour="showcase-agent-mode" data-testid="showcase-agent-mode">
						<Group gap={6} align="center">
							<IconRobot size={14} color="var(--mantine-color-orange-6)" />
							<Text size="xs" fw={600} c="orange">
								{t("onboarding.showcase.agentMode.label")}
							</Text>
						</Group>
						<Group gap={6}>
							<UnstyledButton style={{ cursor: "default" }}>
								<Badge variant="outline" color="gray" size="sm">
									{t("onboarding.showcase.agentMode.defaultAssistant")}
								</Badge>
							</UnstyledButton>
							<Text size="xs" c="dimmed">→</Text>
							<Badge variant="light" color="orange" size="sm">
								{t("onboarding.showcase.agentMode.sampleAgent")}
							</Badge>
						</Group>
						<Text size="xs" c="dimmed">
							{t("onboarding.showcase.agentMode.description")}
						</Text>
					</Stack>

					{/* Feature-discovery cards — point new testers at surfaces beyond chat. Purely illustrative. */}
					<Stack gap={4} data-tour="showcase-knowledge-base" data-testid="showcase-knowledge-base">
						<Group gap={6} align="center">
							<IconDatabase size={14} color="var(--mantine-color-blue-6)" />
							<Text size="xs" fw={600} c="blue">
								{t("onboarding.showcase.knowledgeBase.label")}
							</Text>
						</Group>
						<Text size="xs" c="dimmed">
							{t("onboarding.showcase.knowledgeBase.description")}
						</Text>
					</Stack>

					<Stack gap={4} data-tour="showcase-image-generation" data-testid="showcase-image-generation">
						<Group gap={6} align="center">
							<IconPhoto size={14} color="var(--mantine-color-grape-6)" />
							<Text size="xs" fw={600} c="grape">
								{t("onboarding.showcase.imageGeneration.label")}
							</Text>
						</Group>
						<Text size="xs" c="dimmed">
							{t("onboarding.showcase.imageGeneration.description")}
						</Text>
					</Stack>

					<Stack gap={4} data-tour="showcase-agents" data-testid="showcase-agents">
						<Group gap={6} align="center">
							<IconUsersGroup size={14} color="var(--mantine-color-orange-6)" />
							<Text size="xs" fw={600} c="orange">
								{t("onboarding.showcase.agents.label")}
							</Text>
						</Group>
						<Text size="xs" c="dimmed">
							{t("onboarding.showcase.agents.description")}
						</Text>
					</Stack>
				</Stack>
			</Card>
		</Box>
	);
}
