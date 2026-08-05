import { Alert, Button, Divider, Group, Stack, Switch, Table, Text, Textarea, TextInput } from "@mantine/core";
import { IconDeviceFloppy, IconInfoCircle, IconX } from "@tabler/icons-react";
import { type Ref, useCallback, useImperativeHandle, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { MarkdownEditorField } from "@/core/ui/components/MarkdownEditorField/MarkdownEditorField";
import { SkillResourcesPanel } from "@/features/skills/components/SkillResourcesPanel";
import {
	SKILL_BODY_GUIDANCE_LINES,
	SKILL_BODY_GUIDANCE_TOKENS,
	type SkillFormValues,
	skillFormSchema,
	type SkillProvenance,
} from "@/features/skills/models/SkillModels";

// Imperative handle so the host dialog can place Save in its sticky footer (outside the form body) yet still
// trigger the form's own validate-then-submit. The footer button calls submit(); validation stays in the form.
export interface SkillFormHandle {
	submit: () => void;
}

interface SkillFormProps {
	initialValues: SkillFormValues;
	isSubmitting: boolean;
	submitError?: string;
	// When true, the enabled toggle is shown (edit). Create has no enabled field (a new skill always persists
	// enabled by the store default) so the toggle is hidden then.
	showEnabledToggle: boolean;
	onSubmit: (values: SkillFormValues) => void;
	onCancel: () => void;
	/** Imperative handle exposing submit() so a host footer can drive submission. */
	ref?: Ref<SkillFormHandle>;
	/** Hides the form's own Cancel/Save buttons when the host (DialogShell footer) renders them instead. */
	hideActions?: boolean;
	/** Reports whether the current values differ from initialValues so the host can guard close/navigation. */
	onDirtyChange?: (isDirty: boolean) => void;
	/** Id of the skill being edited. Drives the bundled-resources panel; absent on create (nothing to list yet). */
	skillId?: string;
	/** Where this skill came from. Shown so the trust decision stays visible long after the import. */
	provenance?: SkillProvenance;
}

// Per-field error map keyed by the form field. The skill schema is a flat object, so the first Zod issue path
// segment IS the field name — this keeps the error lookup type-safe (no index-signature dotted-access lint).
type SkillFieldErrors = Partial<Record<keyof SkillFormValues, string>>;

// Create/edit form for a node skill (SKILL.md): name + description inputs plus a markdown body editor. Controlled
// Mantine inputs validated with the shared Zod schema on submit. Description + body are sent to the agent's model
// on demand at run time, so a privacy note is shown. The enabled toggle is only meaningful on edit.
export function SkillForm({
	initialValues,
	isSubmitting,
	submitError,
	showEnabledToggle,
	onSubmit,
	onCancel,
	ref,
	hideActions = false,
	onDirtyChange,
	skillId,
	provenance,
}: SkillFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<SkillFormValues>(initialValues);
	const [errors, setErrors] = useState<SkillFieldErrors>({});

	// Update values AND report the resulting dirty state to the host in the same event-driven step. Dirty = current
	// values differ from the mount snapshot (the page keys this component by editor target so initialValues is stable
	// for the editor session); reporting it from the updater rather than a useEffect that watches state avoids the
	// extra parent re-render the effect-sync pattern causes. A JSON compare is sufficient — the shape is plain data.
	const updateValues = useCallback(
		(updater: (current: SkillFormValues) => SkillFormValues) => {
			setValues((current) => {
				const next = updater(current);
				onDirtyChange?.(JSON.stringify(next) !== JSON.stringify(initialValues));
				return next;
			});
		},
		[initialValues, onDirtyChange],
	);

	// Report the initial (clean) dirty state once on mount. The page keys this component per editor target, so a
	// fresh mount always starts clean. Done as a one-shot render-time call (ref-guarded) rather than a useEffect that
	// re-syncs on every change — the latter forces an extra parent re-render per keystroke.
	const didReportInitialDirty = useRef(false);
	if (!didReportInitialDirty.current) {
		didReportInitialDirty.current = true;
		onDirtyChange?.(JSON.stringify(values) !== JSON.stringify(initialValues));
	}

	const handleBodyChange = useCallback(
		(value: string) => {
			updateValues((current) => ({ ...current, body: value }));
		},
		[updateValues],
	);

	const handleSubmit = useCallback(() => {
		const result = skillFormSchema.safeParse(values);
		if (!result.success) {
			const nextErrors: SkillFieldErrors = {};
			for (const issue of result.error.issues) {
				const key = issue.path[0];
				if (typeof key === "string") {
					nextErrors[key as keyof SkillFormValues] = issue.message;
				}
			}
			setErrors(nextErrors);
			return;
		}

		setErrors({});
		onSubmit(result.data);
	}, [onSubmit, values]);

	useImperativeHandle(ref, () => ({ submit: handleSubmit }), [handleSubmit]);

	// Map the name error to a specific message: a blank name and a pattern violation need different copy. An empty
	// name trips BOTH the min(1) and the regex rules, so the message is keyed off the current value (blank → required,
	// otherwise → invalid pattern) rather than the issue order.
	const nameIssue = errors.name;
	const nameError = useMemo(() => {
		if (!nameIssue) {
			return undefined;
		}
		return values.name.trim().length === 0
			? t("pages.skills.form.name.required", "Name is required.")
			: t("pages.skills.form.name.invalid", "Use lowercase letters and digits separated by single dashes (no leading, trailing or doubled dash).");
	}, [nameIssue, values.name, t]);

	// Body budget against the Agent Skills specification's authoring guidance (<500 lines / <5k tokens). Neither is a
	// hard cap — the backend accepts up to SKILL_BODY_MAX — so this is a hint, not an error. The token figure is the
	// usual ~4-chars-per-token approximation; it only has to be right enough to say "this is getting expensive".
	const bodyBudget = useMemo(() => {
		const lines = values.body.length === 0 ? 0 : values.body.split("\n").length;
		const estimatedTokens = Math.ceil(values.body.length / 4);
		return { estimatedTokens, isOverGuidance: lines > SKILL_BODY_GUIDANCE_LINES || estimatedTokens > SKILL_BODY_GUIDANCE_TOKENS, lines };
	}, [values.body]);

	const metadataEntries = Object.entries(values.metadata ?? {});

	return (
		<Stack gap="md" data-testid="skill-form">
			<Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} data-testid="skill-form-privacy-note">
				{t(
					"pages.skills.form.privacyNote",
					"Skills are sent to the agent's model when it loads them. If that model is a cloud provider, the skill content leaves this node.",
				)}
			</Alert>
			{provenance?.origin === "Imported" ? (
				<Alert color="red" variant="light" icon={<IconInfoCircle size={16} />} data-testid="skill-form-imported-note">
					{t(
						"pages.skills.form.importedNote",
						"This skill was imported from {{source}}. Its instructions are third-party content this node never validated, and they run with your agent's tool access once enabled.",
						{ source: provenance.sourceUri ?? t("pages.skills.list.importedUnknownSource", "an unknown source") },
					)}
				</Alert>
			) : null}
			<TextInput
				label={t("pages.skills.form.name.label", "Name")}
				description={t("pages.skills.form.name.description", "Lowercase identifier (e.g. invoice-review).")}
				placeholder={t("pages.skills.form.name.placeholder", "invoice-review")}
				value={values.name}
				required={true}
				error={nameError}
				onChange={(event) => {
					const value = event.currentTarget.value;
					updateValues((current) => ({ ...current, name: value }));
				}}
				data-testid="skill-form-name"
			/>
			<Textarea
				label={t("pages.skills.form.description.label", "Description")}
				description={t(
					"pages.skills.form.description.description",
					"Short summary the model sees to decide whether to load this skill.",
				)}
				placeholder={t("pages.skills.form.description.placeholder", "How to review supplier invoices.")}
				value={values.description}
				required={true}
				autosize={true}
				minRows={2}
				error={errors.description ? t("pages.skills.form.description.required", "Description is required.") : undefined}
				onChange={(event) => {
					const value = event.currentTarget.value;
					updateValues((current) => ({ ...current, description: value }));
				}}
				data-testid="skill-form-description"
			/>
			<MarkdownEditorField
				label={t("pages.skills.form.body.label", "Body")}
				description={t(
					"pages.skills.form.body.description",
					"The full skill instructions, loaded on demand by the agent's model.",
				)}
				placeholder={t("pages.skills.form.body.placeholder", "# How to review an invoice\n\n1. …")}
				value={values.body}
				required={true}
				minRows={6}
				error={errors.body ? t("pages.skills.form.body.required", "Body is required.") : undefined}
				onChange={handleBodyChange}
				data-testid="skill-form-body"
			/>
			<Text size="xs" c={bodyBudget.isOverGuidance ? "orange" : "dimmed"} data-testid="skill-form-body-budget">
				{t("pages.skills.form.body.budget", "{{lines}} lines · ~{{tokens}} tokens", {
					lines: bodyBudget.lines.toLocaleString(),
					tokens: bodyBudget.estimatedTokens.toLocaleString(),
				})}
				{bodyBudget.isOverGuidance
					? ` · ${t("pages.skills.form.body.overGuidance", "the specification suggests staying under {{lines}} lines and {{tokens}} tokens", {
							lines: SKILL_BODY_GUIDANCE_LINES,
							tokens: SKILL_BODY_GUIDANCE_TOKENS,
						})}`
					: null}
			</Text>

			<Divider
				label={t("pages.skills.form.frontmatterSection", "Frontmatter")}
				labelPosition="left"
				data-testid="skill-form-frontmatter-divider"
			/>
			<Group grow={true} align="flex-start">
				<TextInput
					label={t("pages.skills.form.license.label", "License")}
					placeholder="MIT"
					value={values.license}
					onChange={(event) => {
						const value = event.currentTarget.value;
						updateValues((current) => ({ ...current, license: value }));
					}}
					data-testid="skill-form-license"
				/>
				<TextInput
					label={t("pages.skills.form.compatibility.label", "Compatibility")}
					placeholder="claude-code >=1.0"
					value={values.compatibility}
					onChange={(event) => {
						const value = event.currentTarget.value;
						updateValues((current) => ({ ...current, compatibility: value }));
					}}
					data-testid="skill-form-compatibility"
				/>
			</Group>
			<TextInput
				label={t("pages.skills.form.allowedTools.label", "Allowed tools")}
				// Deliberately blunt: this node does not enforce the field. Wording it as a restriction would claim a
				// control we do not have — the skill's instructions run with whatever tool access the agent already has.
				description={t(
					"pages.skills.form.allowedTools.description",
					"Space-separated list declared by the skill's author. Shown for information only — this node neither grants nor restricts any tool based on it.",
				)}
				placeholder="read_file list_files"
				value={values.allowedTools}
				onChange={(event) => {
					const value = event.currentTarget.value;
					updateValues((current) => ({ ...current, allowedTools: value }));
				}}
				data-testid="skill-form-allowed-tools"
			/>
			{metadataEntries.length > 0 ? (
				<Stack gap={4} data-testid="skill-form-metadata">
					<Text size="sm" fw={600}>
						{t("pages.skills.form.metadata.label", "Metadata")}
					</Text>
					<Table withTableBorder={true} verticalSpacing={4} horizontalSpacing="sm">
						<Table.Tbody>
							{metadataEntries.map(([key, value]) => (
								<Table.Tr key={key}>
									<Table.Td>
										<Text size="xs" ff="monospace">
											{key}
										</Text>
									</Table.Td>
									<Table.Td>
										<Text size="xs">{value}</Text>
									</Table.Td>
								</Table.Tr>
							))}
						</Table.Tbody>
					</Table>
					<Text size="xs" c="dimmed">
						{t("pages.skills.form.metadata.readOnly", "Carried through unchanged from the skill's frontmatter.")}
					</Text>
				</Stack>
			) : null}
			{skillId ? <SkillResourcesPanel skillId={skillId} /> : null}

			{showEnabledToggle ? (
				<Switch
					label={t("pages.skills.form.enabled.label", "Enabled")}
					description={t(
						"pages.skills.form.enabled.description",
						"Disabled skills are never loaded by an agent, even if still assigned.",
					)}
					checked={values.enabled}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						updateValues((current) => ({ ...current, enabled: checked }));
					}}
					data-testid="skill-form-enabled"
				/>
			) : null}

			{submitError ? (
				<Alert color="red" data-testid="skill-form-submit-error">
					{submitError}
				</Alert>
			) : null}
			{hideActions ? null : (
				<Group justify="flex-end">
					<Button
						variant="subtle"
						leftSection={<IconX size={16} />}
						onClick={onCancel}
						disabled={isSubmitting}
						data-testid="skill-form-cancel"
					>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						leftSection={<IconDeviceFloppy size={16} />}
						onClick={handleSubmit}
						loading={isSubmitting}
						data-testid="skill-form-submit"
					>
						{t("common.save", "Save")}
					</Button>
				</Group>
			)}
		</Stack>
	);
}
