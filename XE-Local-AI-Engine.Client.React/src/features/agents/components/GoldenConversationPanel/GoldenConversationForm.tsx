import { Button, Group, Paper, Stack, Textarea, TextInput } from "@mantine/core";
import { IconX } from "@tabler/icons-react";
import { useCallback, useReducer } from "react";
import { useTranslation } from "react-i18next";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import {
	type GoldenFormAction,
	type GoldenFormState,
	parseTurnLine,
	toLines,
} from "@/features/agents/models/GoldenConversationFormHelpers";
import {
	type CreateGoldenConversationRequestDto,
	findGoldenFieldOverLimit,
	GOLDEN_ASSERTION_MAX,
	GOLDEN_INPUT_TURNS_MAX,
	GOLDEN_RUBRIC_MAX,
	GOLDEN_TITLE_MAX,
	type GoldenAssertion,
} from "@/features/agents/models/GoldenConversationModels";

// The cap for each create-request field, surfaced in the "too long" validation message.
const GOLDEN_FIELD_MAX = {
	title: GOLDEN_TITLE_MAX,
	inputTurns: GOLDEN_INPUT_TURNS_MAX,
	assertion: GOLDEN_ASSERTION_MAX,
	rubric: GOLDEN_RUBRIC_MAX,
} as const;

interface GoldenConversationFormProps {
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (request: CreateGoldenConversationRequestDto) => void;
	onCancel: () => void;
}

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
export function GoldenConversationForm({ isSubmitting, submitError, onSubmit, onCancel }: GoldenConversationFormProps) {
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
				{validationError ? <InlineErrorAlert message={validationError} data-testid="golden-form-validation-error" /> : null}
				{submitError ? <InlineErrorAlert message={submitError} data-testid="golden-form-submit-error" /> : null}
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
