import { ActionIcon, Alert, Badge, Button, Group, Loader, Paper, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconPlus, IconSparkles, IconTrash, IconX } from "@tabler/icons-react";
import { useCallback, useReducer, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import {
	type CreateGoldenConversationRequestDto,
	findGoldenFieldOverLimit,
	type GoldenAssertion,
	type GoldenConversation,
	GOLDEN_ASSERTION_MAX,
	GOLDEN_INPUT_TURNS_MAX,
	GOLDEN_RUBRIC_MAX,
	GOLDEN_TITLE_MAX,
	type GoldenTurn,
} from "@/features/agents/models/GoldenConversationModels";
import {
	useApproveGolden,
	useCreateGoldenConversation,
	useDeleteGoldenConversation,
	useGoldenConversations,
	useHarvestGolden,
} from "@/features/agents/queries/useGoldenConversations";

interface GoldenConversationPanelProps {
	// The agent whose golden conversation set is managed. Rendered by the parent only when agentManagement is on;
	// it also guards internally so it can never render its surface when the capability is off.
	agentDefinitionId: string;
	agentName: string;
	// FE-static capability gate (folded under agentManagement). When false the panel renders nothing.
	enabled: boolean;
}

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

// The cap for each create-request field, surfaced in the "too long" validation message.
const GOLDEN_FIELD_MAX = {
	title: GOLDEN_TITLE_MAX,
	inputTurns: GOLDEN_INPUT_TURNS_MAX,
	assertion: GOLDEN_ASSERTION_MAX,
	rubric: GOLDEN_RUBRIC_MAX,
} as const;

// Parse a newline-separated textarea into trimmed non-empty lines (used for input turns + phrase lists).
function toLines(value: string): string[] {
	return value
		.split("\n")
		.map((line) => line.trim())
		.filter((line) => line.length > 0);
}

// A free-text input-turn line authored as "role: text" → { role, text }. A line without a colon is treated as a
// user turn (the common case), so an operator can type plain prompts. Defensive against an empty role.
function parseTurnLine(line: string): GoldenTurn {
	const separator = line.indexOf(":");
	if (separator < 0) {
		return { role: "user", text: line };
	}
	const role = line.slice(0, separator).trim();
	const text = line.slice(separator + 1).trim();
	return { role: role.length > 0 ? role : "user", text };
}

// Per-agent golden conversation management. Lists the agent's golden cases (title + turn count +
// assertion/rubric presence), offers an add form (title + input turns + required/forbidden phrases and/or rubric),
// and delete. Capability-gated under agentManagement — when `enabled` is false it renders nothing. The golden set
// gates promotion: the eval runner replays each case against the candidate playbook action.
export function GoldenConversationPanel({ agentDefinitionId, agentName, enabled }: GoldenConversationPanelProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const [formOpen, setFormOpen] = useState(false);

	const goldenQuery = useGoldenConversations(enabled ? agentDefinitionId : null);
	const createMutation = useCreateGoldenConversation(agentDefinitionId);
	const deleteMutation = useDeleteGoldenConversation(agentDefinitionId);
	const harvestMutation = useHarvestGolden(agentDefinitionId);
	const approveMutation = useApproveGolden(agentDefinitionId);

	const isMutating =
		createMutation.isPending || deleteMutation.isPending || harvestMutation.isPending || approveMutation.isPending;

	const cases = goldenQuery.data ?? [];
	// Harvested candidates awaiting operator review (inert until approved); everything else is the active list.
	const pendingCases = cases.filter((goldenCase) => goldenCase.source === "harvested" && !goldenCase.enabled);
	const activeCases = cases.filter((goldenCase) => !(goldenCase.source === "harvested" && !goldenCase.enabled));

	const closeForm = useCallback(() => setFormOpen(false), []);

	const handleHarvest = useCallback(() => {
		harvestMutation.mutate(undefined, {
			onSuccess: (result) => {
				toast.success(
					t(
						"pages.agents.golden.harvest.success",
						"{{created}} proposed, {{duplicate}} already harvested, {{skipped}} skipped.",
						{ created: result.createdCount, duplicate: result.duplicateCount, skipped: result.skippedCount },
					),
				);
			},
			onError: (error) => toast.error(errorMessage(error, t("pages.agents.golden.errors.harvest", "Could not harvest golden cases."))),
		});
	}, [harvestMutation, t]);

	const handleApprove = useCallback(
		(goldenCase: GoldenConversation) => {
			approveMutation.mutate(goldenCase.id, {
				onError: (error) =>
					toast.error(errorMessage(error, t("pages.agents.golden.errors.approve", "Could not approve the golden case."))),
			});
		},
		[approveMutation, t],
	);

	const handleCreate = useCallback(
		(request: CreateGoldenConversationRequestDto) => {
			createMutation.mutate(request, { onSuccess: closeForm });
		},
		[closeForm, createMutation],
	);

	const handleDelete = useCallback(
		async (goldenCase: GoldenConversation) => {
			const confirmed = await confirm({
				title: t("pages.agents.golden.delete.title", "Delete golden case"),
				description: t("pages.agents.golden.delete.description", "Delete '{{title}}'? This cannot be undone.", {
					title: goldenCase.title,
				}),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				deleteMutation.mutate(goldenCase.id, {
					onError: (error) =>
						toast.error(errorMessage(error, t("pages.agents.golden.errors.delete", "Could not delete the golden case."))),
				});
			}
		},
		[confirm, deleteMutation, t],
	);

	if (!enabled) {
		return null;
	}

	const createError = createMutation.error
		? errorMessage(createMutation.error, t("pages.agents.golden.errors.save", "Could not save the golden case."))
		: undefined;

	return (
		<Paper withBorder={true} radius="md" p="md" data-testid={`golden-panel-${agentDefinitionId}`}>
			<Stack gap="sm">
				<Group justify="space-between" align="flex-start">
					<Stack gap={2}>
						<Text fw={600}>{t("pages.agents.golden.title", "Golden conversations")}</Text>
						<Text size="xs" c="dimmed">
							{t(
								"pages.agents.golden.subtitle",
								"Author golden cases that gate promotion: a candidate action must not regress them before {{name}} can enable it.",
								{ name: agentName },
							)}
						</Text>
					</Stack>
					{!formOpen ? (
						<Group gap="xs">
							<Button
								size="xs"
								variant="light"
								leftSection={<IconSparkles size={14} />}
								onClick={handleHarvest}
								loading={harvestMutation.isPending}
								disabled={isMutating}
								data-testid="golden-harvest-button"
							>
								{t("pages.agents.golden.harvest.button", "Harvest from 👍")}
							</Button>
							<Button
								size="xs"
								variant="light"
								leftSection={<IconPlus size={14} />}
								onClick={() => setFormOpen(true)}
								disabled={isMutating}
								data-testid="golden-add-button"
							>
								{t("pages.agents.golden.addButton", "Add golden case")}
							</Button>
						</Group>
					) : null}
				</Group>

				{formOpen ? (
					<GoldenConversationForm
						isSubmitting={createMutation.isPending}
						submitError={createError}
						onSubmit={handleCreate}
						onCancel={closeForm}
					/>
				) : null}

				{goldenQuery.isLoading ? (
					<Group gap="sm" data-testid="golden-loading">
						<Loader size="sm" />
						<Text c="dimmed" size="sm">
							{t("pages.agents.golden.loading", "Loading golden cases…")}
						</Text>
					</Group>
				) : null}

				{goldenQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="golden-list-error">
						{errorMessage(goldenQuery.error, t("pages.agents.golden.errors.load", "Could not load golden cases."))}
					</Alert>
				) : null}

				{!goldenQuery.isLoading && !goldenQuery.error && cases.length === 0 && !formOpen ? (
					<Text size="sm" c="dimmed" data-testid="golden-empty">
						{t("pages.agents.golden.empty", "No golden cases yet. Add one so promotion can be eval-gated.")}
					</Text>
				) : null}

				{pendingCases.length > 0 ? (
					<Stack gap="xs" data-testid="golden-pending-section">
						<Text size="sm" fw={600}>
							{t("pages.agents.golden.pending.heading", "Harvested (pending review)")}
						</Text>
						<Text size="xs" c="dimmed">
							{t(
								"pages.agents.golden.pending.hint",
								"Review each harvested candidate, then approve it into the active golden set or reject it.",
							)}
						</Text>
						{pendingCases.map((goldenCase) => (
							<GoldenPendingRow
								key={goldenCase.id}
								goldenCase={goldenCase}
								disabled={isMutating}
								onApprove={() => handleApprove(goldenCase)}
								onReject={() => handleDelete(goldenCase)}
							/>
						))}
					</Stack>
				) : null}

				{activeCases.map((goldenCase) => (
					<GoldenConversationRow
						key={goldenCase.id}
						goldenCase={goldenCase}
						disabled={isMutating}
						onDelete={() => handleDelete(goldenCase)}
					/>
				))}
			</Stack>
		</Paper>
	);
}

// First-turn preview of a harvested candidate's input conversation, used in the pending-review sub-section so the
// operator can judge the case without expanding it: the count plus a truncated first turn.
const PENDING_TURN_PREVIEW_MAX = 120;
const PENDING_RUBRIC_PREVIEW_MAX = 200;

function truncate(value: string, max: number): string {
	return value.length > max ? `${value.slice(0, max)}…` : value;
}

interface GoldenConversationRowProps {
	goldenCase: GoldenConversation;
	disabled: boolean;
	onDelete: () => void;
}

// One golden case: title, the input-turn count, and presence badges for the scoring signals (assertion / rubric).
function GoldenConversationRow({ goldenCase, disabled, onDelete }: GoldenConversationRowProps) {
	const { t } = useTranslation();

	return (
		<Paper withBorder={true} p="xs" key={goldenCase.id} data-testid={`golden-case-${goldenCase.id}`}>
			<Group justify="space-between" align="flex-start" wrap="nowrap">
				<Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
					<Group gap="xs" align="center" wrap="wrap">
						<Text size="sm" fw={600}>
							{goldenCase.title}
						</Text>
						{!goldenCase.enabled ? (
							<Badge size="xs" variant="outline" color="gray" data-testid={`golden-case-disabled-${goldenCase.id}`}>
								{t("pages.agents.golden.disabledBadge", "disabled")}
							</Badge>
						) : null}
						{goldenCase.source === "harvested" ? (
							<Badge size="xs" variant="light" color="teal" data-testid={`golden-case-harvested-${goldenCase.id}`}>
								{t("pages.agents.golden.harvestedBadge", "harvested")}
							</Badge>
						) : null}
					</Group>
					<Group gap="xs" align="center" wrap="wrap">
						<Text size="xs" c="dimmed" data-testid={`golden-case-turns-${goldenCase.id}`}>
							{t("pages.agents.golden.turnCount", "{{count}} input turns", { count: goldenCase.inputTurns.length })}
						</Text>
						{goldenCase.assertion ? (
							<Badge size="xs" variant="light" color="blue" data-testid={`golden-case-assertion-${goldenCase.id}`}>
								{t("pages.agents.golden.hasAssertion", "assertion")}
							</Badge>
						) : null}
						{goldenCase.rubric ? (
							<Badge size="xs" variant="light" color="grape" data-testid={`golden-case-rubric-${goldenCase.id}`}>
								{t("pages.agents.golden.hasRubric", "rubric")}
							</Badge>
						) : null}
					</Group>
				</Stack>
				<ActionIcon
					aria-label={t("pages.agents.golden.deleteAria", "Delete golden case")}
					variant="subtle"
					color="red"
					size="sm"
					disabled={disabled}
					onClick={onDelete}
					data-testid={`golden-delete-${goldenCase.id}`}
				>
					<IconTrash size={14} />
				</ActionIcon>
			</Group>
		</Paper>
	);
}

interface GoldenPendingRowProps {
	goldenCase: GoldenConversation;
	disabled: boolean;
	onApprove: () => void;
	onReject: () => void;
}

// One harvested candidate in the pending-review sub-section: title, a turns preview (count + first turn), the seeded
// rubric (truncated), and Approve/Reject. Approve flips it into the active golden set; Reject deletes it (same
// ownership-guarded delete as a manual case, with the existing confirm pattern).
function GoldenPendingRow({ goldenCase, disabled, onApprove, onReject }: GoldenPendingRowProps) {
	const { t } = useTranslation();

	const firstTurn = goldenCase.inputTurns[0];
	const rubricPreview = goldenCase.rubric ? truncate(goldenCase.rubric, PENDING_RUBRIC_PREVIEW_MAX) : null;

	return (
		<Paper withBorder={true} p="xs" data-testid={`golden-pending-${goldenCase.id}`}>
			<Stack gap={6}>
				<Text size="sm" fw={600}>
					{goldenCase.title}
				</Text>
				<Text size="xs" c="dimmed" data-testid={`golden-pending-turns-${goldenCase.id}`}>
					{firstTurn
						? t("pages.agents.golden.pending.turnsPreview", "{{count}} turns · {{role}}: {{text}}", {
								count: goldenCase.inputTurns.length,
								role: firstTurn.role,
								text: truncate(firstTurn.text, PENDING_TURN_PREVIEW_MAX),
							})
						: t("pages.agents.golden.pending.turnCount", "{{count}} input turns", {
								count: goldenCase.inputTurns.length,
							})}
				</Text>
				{rubricPreview ? (
					<Text size="xs" c="dimmed" data-testid={`golden-pending-rubric-${goldenCase.id}`}>
						{t("pages.agents.golden.pending.rubric", "Rubric: {{rubric}}", { rubric: rubricPreview })}
					</Text>
				) : null}
				<Group justify="flex-end" gap="xs">
					<Button
						size="xs"
						variant="subtle"
						color="red"
						leftSection={<IconTrash size={14} />}
						onClick={onReject}
						disabled={disabled}
						data-testid={`golden-pending-reject-${goldenCase.id}`}
					>
						{t("pages.agents.golden.pending.reject", "Reject")}
					</Button>
					<Button
						size="xs"
						variant="light"
						color="teal"
						leftSection={<IconCheck size={14} />}
						onClick={onApprove}
						disabled={disabled}
						data-testid={`golden-pending-approve-${goldenCase.id}`}
					>
						{t("pages.agents.golden.pending.approve", "Approve")}
					</Button>
				</Group>
			</Stack>
		</Paper>
	);
}

interface GoldenConversationFormProps {
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (request: CreateGoldenConversationRequestDto) => void;
	onCancel: () => void;
}

// Consolidated form state for the golden-case add form. The text fields plus the on-submit validation message live in
// one object so a logical update never fans out into separate renders (was 6 useState calls).
interface GoldenFormState {
	title: string;
	turnsText: string;
	requiredText: string;
	forbiddenText: string;
	rubric: string;
	validationError: string | null;
}

type GoldenFormAction =
	| { type: "setField"; field: "title" | "turnsText" | "requiredText" | "forbiddenText" | "rubric"; value: string }
	| { type: "setValidationError"; value: string | null };

const initialGoldenFormState: GoldenFormState = {
	title: "",
	turnsText: "",
	requiredText: "",
	forbiddenText: "",
	rubric: "",
	validationError: null,
};

function goldenFormReducer(state: GoldenFormState, action: GoldenFormAction): GoldenFormState {
	switch (action.type) {
		case "setField":
			return { ...state, [action.field]: action.value };
		case "setValidationError":
			return { ...state, validationError: action.value };
		default:
			return state;
	}
}

// Inline add form for a golden case. Input turns are one "role: text" line per turn (a colon-less line is a user
// turn). Required/forbidden phrases are one phrase per line. A case must carry a title + ≥1 input turn and at
// least one scoring signal (an assertion phrase or a rubric) — validated on submit, mirroring the backend rule.
function GoldenConversationForm({ isSubmitting, submitError, onSubmit, onCancel }: GoldenConversationFormProps) {
	const { t } = useTranslation();

	const [state, dispatch] = useReducer(goldenFormReducer, initialGoldenFormState);
	const { title, turnsText, requiredText, forbiddenText, rubric, validationError } = state;

	const handleSubmit = useCallback(() => {
		const trimmedTitle = title.trim();
		const turnLines = toLines(turnsText);
		const requiredPhrases = toLines(requiredText);
		const forbiddenPhrases = toLines(forbiddenText);
		const trimmedRubric = rubric.trim();

		if (trimmedTitle.length === 0) {
			dispatch({ type: "setValidationError", value: t("pages.agents.golden.form.titleRequired", "Title is required.") });
			return;
		}
		if (turnLines.length === 0) {
			dispatch({
				type: "setValidationError",
				value: t("pages.agents.golden.form.turnsRequired", "At least one input turn is required."),
			});
			return;
		}

		const hasAssertion = requiredPhrases.length > 0 || forbiddenPhrases.length > 0;
		const hasRubric = trimmedRubric.length > 0;
		if (!hasAssertion && !hasRubric) {
			dispatch({
				type: "setValidationError",
				value: t("pages.agents.golden.form.signalRequired", "Add at least a required/forbidden phrase or a rubric."),
			});
			return;
		}

		const assertion: GoldenAssertion | undefined = hasAssertion ? { requiredPhrases, forbiddenPhrases } : undefined;

		const request: CreateGoldenConversationRequestDto = {
			title: trimmedTitle,
			inputTurns: turnLines.map(parseTurnLine),
			...(assertion ? { assertion } : {}),
			...(hasRubric ? { rubric: trimmedRubric } : {}),
		};

		// Reject over-long fields before the POST, mirroring the backend caps (like the playbook behavior cap) so the
		// operator gets a precise field message instead of a generic 400.
		const overLimit = findGoldenFieldOverLimit(request);
		if (overLimit !== null) {
			dispatch({
				type: "setValidationError",
				value: t(`pages.agents.golden.form.tooLong.${overLimit}`, "{{field}} is too long (max {{max}} characters).", {
					field: overLimit,
					max: GOLDEN_FIELD_MAX[overLimit],
				}),
			});
			return;
		}

		dispatch({ type: "setValidationError", value: null });
		onSubmit(request);
	}, [forbiddenText, onSubmit, requiredText, rubric, t, title, turnsText]);

	return (
		<Paper withBorder={true} p="sm" data-testid="golden-form">
			<Stack gap="sm">
				<TextInput
					label={t("pages.agents.golden.form.title.label", "Title")}
					placeholder={t("pages.agents.golden.form.title.placeholder", "Short operator label")}
					value={title}
					required={true}
					onChange={(event) => dispatch({ type: "setField", field: "title", value: event.currentTarget.value })}
					data-testid="golden-form-title"
				/>
				<Textarea
					label={t("pages.agents.golden.form.turns.label", "Input turns")}
					description={t(
						"pages.agents.golden.form.turns.description",
						"One turn per line as 'role: text'. A line without a colon is treated as a user turn.",
					)}
					placeholder={t("pages.agents.golden.form.turns.placeholder", "user: Summarize the document…")}
					value={turnsText}
					required={true}
					autosize={true}
					minRows={2}
					onChange={(event) => dispatch({ type: "setField", field: "turnsText", value: event.currentTarget.value })}
					data-testid="golden-form-turns"
				/>
				<Group grow={true} align="flex-start">
					<Textarea
						label={t("pages.agents.golden.form.required.label", "Required phrases")}
						description={t("pages.agents.golden.form.required.description", "One per line. All must be present.")}
						value={requiredText}
						autosize={true}
						minRows={1}
						onChange={(event) => dispatch({ type: "setField", field: "requiredText", value: event.currentTarget.value })}
						data-testid="golden-form-required"
					/>
					<Textarea
						label={t("pages.agents.golden.form.forbidden.label", "Forbidden phrases")}
						description={t("pages.agents.golden.form.forbidden.description", "One per line. None may be present.")}
						value={forbiddenText}
						autosize={true}
						minRows={1}
						onChange={(event) => dispatch({ type: "setField", field: "forbiddenText", value: event.currentTarget.value })}
						data-testid="golden-form-forbidden"
					/>
				</Group>
				<Textarea
					label={t("pages.agents.golden.form.rubric.label", "Rubric")}
					description={t(
						"pages.agents.golden.form.rubric.description",
						"Optional judge rubric (used when no assertion applies).",
					)}
					value={rubric}
					autosize={true}
					minRows={1}
					onChange={(event) => dispatch({ type: "setField", field: "rubric", value: event.currentTarget.value })}
					data-testid="golden-form-rubric"
				/>
				{validationError ? (
					<Alert color="red" data-testid="golden-form-validation-error">
						{validationError}
					</Alert>
				) : null}
				{submitError ? (
					<Alert color="red" data-testid="golden-form-submit-error">
						{submitError}
					</Alert>
				) : null}
				<Group justify="flex-end">
					<Button
						variant="subtle"
						size="xs"
						leftSection={<IconX size={14} />}
						onClick={onCancel}
						disabled={isSubmitting}
						data-testid="golden-form-cancel"
					>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button size="xs" onClick={handleSubmit} loading={isSubmitting} data-testid="golden-form-submit">
						{t("common.save", "Save")}
					</Button>
				</Group>
			</Stack>
		</Paper>
	);
}
