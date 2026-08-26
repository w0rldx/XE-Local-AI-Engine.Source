import { Alert, Badge, Checkbox, List, Radio, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type {
	XeLocalAiEngineClientEndpointsSkillsV1SkillImportPreviewResponse,
	XeLocalAiEngineClientServicesAgentsSkillImportConflictResolution,
} from "@/core/api/generated";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { SkillImportCandidateCard } from "@/features/skills/components/SkillImportCandidateCard";

interface SkillImportOutcome {
	readonly name: string;
	readonly status: string;
	readonly reason?: string | null;
}

interface SkillImportPreviewPresentationProps {
	readonly warningId: string;
	readonly acknowledged: boolean;
	readonly onAcknowledgedChange: (acknowledged: boolean) => void;
	readonly report: XeLocalAiEngineClientEndpointsSkillsV1SkillImportPreviewResponse | null;
	readonly outcomes: readonly SkillImportOutcome[] | null;
	readonly selected: ReadonlySet<string>;
	readonly selectedCount: number;
	readonly hasSelectedConflict: boolean;
	readonly conflictResolution: XeLocalAiEngineClientServicesAgentsSkillImportConflictResolution;
	readonly onConflictResolutionChange: (value: XeLocalAiEngineClientServicesAgentsSkillImportConflictResolution) => void;
	readonly onToggleCandidate: (name: string, selected: boolean) => void;
}

export function SkillImportPreviewPresentation({
	warningId,
	acknowledged,
	onAcknowledgedChange,
	report,
	outcomes,
	selected,
	selectedCount,
	hasSelectedConflict,
	conflictResolution,
	onConflictResolutionChange,
	onToggleCandidate,
}: SkillImportPreviewPresentationProps) {
	const { t } = useTranslation();

	return (
		<>
			<Alert
				id={warningId}
				color="red"
				variant="light"
				icon={<IconAlertTriangle size={16} />}
				title={t("pages.skills.import.warning.title", "Imported skills are untrusted content")}
				data-testid="skill-import-warning"
			>
				<Stack gap={6}>
					<Text size="sm">
						{t(
							"pages.skills.import.warning.untrusted",
							"Skills you import are third-party instructions. This node does not validate, scan or sandbox them. A skill's body is injected verbatim into your agent's context and can attempt to redirect the agent to do something other than what its description says.",
						)}
					</Text>
					<Text size="sm" fw={700} data-testid="skill-import-warning-consequence">
						{t(
							"pages.skills.import.warning.consequence",
							"An enabled skill's instructions run with your agent's tool access — including reading your knowledge base and local workspace files without a further prompt.",
						)}
					</Text>
					<Text size="sm">
						{t(
							"pages.skills.import.warning.posture",
							"Scripts are never imported. Everything else is shown to you exactly as it will be stored — read the full body and every resource below, then decide.",
						)}
					</Text>
					<Text size="sm">
						{t(
							"pages.skills.import.warning.disabled",
							"Imported skills arrive disabled. Enabling one is a separate, deliberate step.",
						)}
					</Text>
				</Stack>
			</Alert>

			<Checkbox
				checked={acknowledged}
				onChange={(event) => onAcknowledgedChange(event.currentTarget.checked)}
				aria-describedby={warningId}
				data-testid="skill-import-acknowledge"
				label={t(
					"pages.skills.import.acknowledge",
					"I understand this content is untrusted and that I am responsible for reviewing it.",
				)}
			/>

			{outcomes ? <SkillImportOutcomes outcomes={outcomes} /> : null}
			{!outcomes && report ? (
				<SkillImportReport
					report={report}
					selected={selected}
					selectedCount={selectedCount}
					hasSelectedConflict={hasSelectedConflict}
					conflictResolution={conflictResolution}
					onConflictResolutionChange={onConflictResolutionChange}
					onToggleCandidate={onToggleCandidate}
				/>
			) : null}
		</>
	);
}

function SkillImportOutcomes({ outcomes }: { readonly outcomes: readonly SkillImportOutcome[] }) {
	const { t } = useTranslation();
	return (
		<Stack gap="xs" data-testid="skill-import-outcomes">
			<Text fw={600}>{t("pages.skills.import.outcomes.title", "Import result")}</Text>
			<List size="sm" withPadding={true}>
				{outcomes.map((outcome) => (
					<List.Item key={outcome.name} data-testid={`skill-import-outcome-${outcome.name}`}>
						<Text component="span" ff="monospace" size="sm">
							{outcome.name}
						</Text>
						<Badge ml="xs" size="sm" variant="light" color={outcome.status === "Skipped" ? "gray" : "teal"}>
							{outcome.status}
						</Badge>
						{outcome.reason ? <Text component="span" size="sm" c="dimmed">{` — ${outcome.reason}`}</Text> : null}
					</List.Item>
				))}
			</List>
			<Text size="sm" c="dimmed">
				{t(
					"pages.skills.import.outcomes.disabledNote",
					"Imported skills are disabled. Open one to review it, then enable it deliberately.",
				)}
			</Text>
		</Stack>
	);
}

interface SkillImportReportProps {
	readonly report: XeLocalAiEngineClientEndpointsSkillsV1SkillImportPreviewResponse;
	readonly selected: ReadonlySet<string>;
	readonly selectedCount: number;
	readonly hasSelectedConflict: boolean;
	readonly conflictResolution: XeLocalAiEngineClientServicesAgentsSkillImportConflictResolution;
	readonly onConflictResolutionChange: (value: XeLocalAiEngineClientServicesAgentsSkillImportConflictResolution) => void;
	readonly onToggleCandidate: (name: string, selected: boolean) => void;
}

function SkillImportReport({
	report,
	selected,
	selectedCount,
	hasSelectedConflict,
	conflictResolution,
	onConflictResolutionChange,
	onToggleCandidate,
}: SkillImportReportProps) {
	const { t } = useTranslation();
	return (
		<Stack gap="md" data-testid="skill-import-report">
			<Text size="sm" c="dimmed">
				{t("pages.skills.import.report.summary", "Source {{source}} · {{found}} skills found · {{selected}} selected", {
					found: report.skills.length,
					selected: selectedCount,
					source: report.sourceUri,
				})}
			</Text>
			{report.warnings.length > 0 ? (
				<Alert color="blue" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="skill-import-report-warnings">
					<List size="sm" withPadding={true}>
						{report.warnings.map((warning) => (
							<List.Item key={warning}>{warning}</List.Item>
						))}
					</List>
				</Alert>
			) : null}
			{hasSelectedConflict ? (
				<Radio.Group
					value={conflictResolution}
					onChange={(value) =>
						onConflictResolutionChange(value as XeLocalAiEngineClientServicesAgentsSkillImportConflictResolution)
					}
					label={t("pages.skills.import.conflict.label", "A selected skill already exists on this node")}
					data-testid="skill-import-conflict-resolution"
				>
					<Stack gap={4} mt={4}>
						<Radio
							value="Skip"
							data-testid="skill-import-conflict-skip"
							label={t("pages.skills.import.conflict.skip", "Skip it and keep what is on this node")}
						/>
						<Radio
							value="Replace"
							data-testid="skill-import-conflict-replace"
							label={t(
								"pages.skills.import.conflict.replace",
								"Replace it — this overwrites the existing skill and loses any local edits",
							)}
						/>
					</Stack>
				</Radio.Group>
			) : null}
			{report.skills.length === 0 ? (
				<EmptyState
					message={t("pages.skills.import.report.empty", "This source contains no skills.")}
					data-testid="skill-import-report-empty"
				/>
			) : null}
			{report.skills.map((candidate) => (
				<SkillImportCandidateCard
					key={candidate.name}
					candidate={candidate}
					selected={selected.has(candidate.name)}
					onToggle={onToggleCandidate}
				/>
			))}
		</Stack>
	);
}
