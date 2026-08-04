import { Badge, Button, Checkbox, Group, Radio, Stack, Text, TextInput, ThemeIcon } from "@mantine/core";
import { IconHelpCircle, IconStar } from "@tabler/icons-react";
import { useMutation } from "@tanstack/react-query";
import { type CSSProperties, useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { withResponseValidation } from "@/core/api/ResponseValidation";
// SWAP POINT: when the OpenAPI regen lands the resolve endpoint, change this import to the generated
// `resolveUserQuestionMutation` from "@/core/api/generated/@tanstack/react-query.gen" — the interim stand-in has the
// same `{ mutationFn }` over `{ body }` shape, so nothing below changes.
import { resolveUserQuestionMutation } from "@/features/chat/api/AskUserQuestionWire";
import type { PendingUserQuestion, UserQuestionAnswer } from "@/features/chat/api/AskUserQuestionWire";
import { CHAT_ACCENT, CHAT_ACCENT_SOFT } from "@/features/chat/components/ChatVisualTokens";
import classes from "@/features/chat/components/ThoughtsSection.module.css";

interface AskUserQuestionCardProps {
	pending: PendingUserQuestion;
}

/**
 * Option value for the free-text row. Real options are addressed by their index in `question.options`, so this
 * sentinel can never collide with one — the "Other…" row is appended by the CLIENT on every question (the model
 * never declares it), matching how Claude Code's own ask-user tool behaves.
 */
const OTHER_VALUE = "other";

/** Per-question working state: the chosen option indices (as strings) plus the free-text value. */
interface QuestionDraft {
	selected: string[];
	other: string;
}

/** A <fieldset> is used purely for its native group semantics; its default chrome is reset away. */
const fieldsetReset: CSSProperties = { border: 0, margin: 0, padding: 0, minInlineSize: 0 };

function isAnswered(draft: QuestionDraft): boolean {
	if (draft.selected.length === 0) {
		return false;
	}

	return !draft.selected.includes(OTHER_VALUE) || draft.other.trim().length > 0;
}

/** Projects the working drafts onto the wire answers — option indices resolve back to their labels. */
function toAnswers(pending: PendingUserQuestion, drafts: readonly QuestionDraft[]): UserQuestionAnswer[] {
	return pending.questions.map((question, index) => {
		const draft = drafts[index] ?? { selected: [], other: "" };
		const selected = draft.selected
			.filter((value) => value !== OTHER_VALUE)
			.map((value) => question.options[Number(value)]?.label)
			.filter((label): label is string => typeof label === "string");
		const other = draft.selected.includes(OTHER_VALUE) ? draft.other.trim() : undefined;

		return { question: question.question, selected, other };
	});
}

/**
 * The inline `ask_user` answer card: the agent has parked its turn on a question and the user answers it here, in the
 * assistant turn itself (deliberately NOT a modal — the composer stays usable). All questions from one tool call
 * render as stacked sections under a single Submit, and the card collapses to a read-only summary of what was chosen
 * once submitted, so scrollback stays a faithful record of the exchange.
 *
 * Rendered by {@link ToolCallCard} in place of its generic waiting body — `ask_user` IS a tool call, so it reuses the
 * one state-driven tool component and the ordered-parts contract is untouched (docs/agent-knowledge.md §5).
 */
export function AskUserQuestionCard({ pending }: AskUserQuestionCardProps) {
	const { t } = useTranslation();
	const [drafts, setDrafts] = useState<QuestionDraft[]>(() => pending.questions.map(() => ({ selected: [], other: "" })));
	// Optimistic: the controls collapse into the summary the instant the user submits, and re-arm if the post fails
	// (same contract as the tool-approval controls in ToolCallCard).
	const [submitted, setSubmitted] = useState<UserQuestionAnswer[] | undefined>(undefined);
	const resolveQuestion = useMutation({ ...withResponseValidation(resolveUserQuestionMutation()) });

	const updateDraft = useCallback((index: number, patch: Partial<QuestionDraft>) => {
		setDrafts((current) => current.map((draft, position) => (position === index ? { ...draft, ...patch } : draft)));
	}, []);

	const complete = useMemo(() => drafts.length === pending.questions.length && drafts.every(isAnswered), [drafts, pending]);

	const handleSubmit = useCallback(() => {
		const answers = toAnswers(pending, drafts);
		setSubmitted(answers);
		resolveQuestion.mutate(
			{ body: { requestId: pending.requestId, answers } },
			// Re-arm the form if the post failed so the user can retry rather than being left with a dismissed
			// prompt on a still-parked turn.
			{ onError: () => setSubmitted(undefined) },
		);
	}, [drafts, pending, resolveQuestion]);

	if (submitted) {
		return (
			<Stack gap={6} className={classes["tool-body"]} data-testid="chat-ask-user-summary">
				{submitted.map((answer) => (
					<Stack gap={0} key={answer.question}>
						<Text size="xs" c="dimmed" fw={600}>
							{answer.question}
						</Text>
						<Text size="sm">{[...answer.selected, ...(answer.other ? [answer.other] : [])].join(", ")}</Text>
					</Stack>
				))}
			</Stack>
		);
	}

	return (
		<Stack gap="sm" className={classes["tool-body"]} data-testid="chat-ask-user-card">
			<Group gap="xs" wrap="nowrap" align="center">
				<ThemeIcon size={22} radius="xl" variant="filled" style={{ background: CHAT_ACCENT_SOFT, color: CHAT_ACCENT }}>
					<IconHelpCircle size={13} />
				</ThemeIcon>
				<Text size="sm" fw={600}>
					{t("chat.askUser.title", "The assistant needs your input")}
				</Text>
			</Group>

			{pending.questions.map((question, index) => {
				const draft = drafts[index] ?? { selected: [], other: "" };
				const otherSelected = draft.selected.includes(OTHER_VALUE);

				return (
					<fieldset
						key={question.question}
						style={fieldsetReset}
						data-testid={`chat-ask-user-question-${index}`}
					>
						<Text component="legend" id={`chat-ask-user-legend-${index}`} size="sm" fw={600} pb={4}>
							{question.header ?? question.question}
						</Text>
						{question.header ? (
							<Text size="sm" c="dimmed" pb={6}>
								{question.question}
							</Text>
						) : null}
						{question.multiSelect ? (
							<Checkbox.Group
								value={draft.selected}
								onChange={(value) => updateDraft(index, { selected: value })}
								aria-labelledby={`chat-ask-user-legend-${index}`}
							>
								<Stack gap={6}>
									{question.options.map((option, optionIndex) => (
										<Checkbox
											key={option.label}
											value={String(optionIndex)}
											label={<OptionLabel label={option.label} recommended={option.recommended} />}
											description={option.description}
											data-testid={`chat-ask-user-option-${index}-${optionIndex}`}
										/>
									))}
									<Checkbox
										value={OTHER_VALUE}
										label={t("chat.askUser.otherOption", "Other…")}
										data-testid={`chat-ask-user-other-${index}`}
									/>
								</Stack>
							</Checkbox.Group>
						) : (
							<Radio.Group
								value={draft.selected[0] ?? ""}
								onChange={(value) => updateDraft(index, { selected: [value] })}
								aria-labelledby={`chat-ask-user-legend-${index}`}
							>
								<Stack gap={6}>
									{question.options.map((option, optionIndex) => (
										<Radio
											key={option.label}
											value={String(optionIndex)}
											label={<OptionLabel label={option.label} recommended={option.recommended} />}
											description={option.description}
											data-testid={`chat-ask-user-option-${index}-${optionIndex}`}
										/>
									))}
									<Radio
										value={OTHER_VALUE}
										label={t("chat.askUser.otherOption", "Other…")}
										data-testid={`chat-ask-user-other-${index}`}
									/>
								</Stack>
							</Radio.Group>
						)}
						{otherSelected ? (
							<TextInput
								mt={6}
								size="xs"
								value={draft.other}
								onChange={(event) => updateDraft(index, { other: event.currentTarget.value })}
								placeholder={t("chat.askUser.otherPlaceholder", "Type your own answer")}
								aria-label={t("chat.askUser.otherPlaceholder", "Type your own answer")}
								data-testid={`chat-ask-user-other-text-${index}`}
							/>
						) : null}
					</fieldset>
				);
			})}

			<Group gap="xs">
				<Button
					size="compact-sm"
					variant="light"
					disabled={!complete}
					loading={resolveQuestion.isPending}
					onClick={handleSubmit}
					data-testid="chat-ask-user-submit"
				>
					{t("chat.askUser.submit", "Send answer")}
				</Button>
				{complete ? null : (
					<Text size="xs" c="dimmed">
						{t("chat.askUser.answerAllHint", "Answer every question to continue.")}
					</Text>
				)}
			</Group>
		</Stack>
	);
}

/** An option's label with the advisory ★ Recommended badge. Recommended is never pre-selected — the user chooses. */
function OptionLabel({ label, recommended }: { label: string; recommended?: boolean }) {
	const { t } = useTranslation();
	if (!recommended) {
		return <Text size="sm">{label}</Text>;
	}

	return (
		<Group gap={6} wrap="nowrap" align="center">
			<Text size="sm">{label}</Text>
			<Badge size="xs" variant="light" radius="sm" leftSection={<IconStar size={9} />} data-testid="chat-ask-user-recommended">
				{t("chat.askUser.recommended", "Recommended")}
			</Badge>
		</Group>
	);
}
