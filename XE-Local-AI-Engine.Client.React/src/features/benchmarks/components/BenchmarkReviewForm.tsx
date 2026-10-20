import { Alert, Button, Card, Group, NumberInput, Stack, Text, Textarea, Title } from "@mantine/core";
import { IconCheck, IconUserCheck, IconX } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import type {
	BenchmarkReviewSubmission,
	BenchmarkRubricCriterion,
	BenchmarkRunDetail,
} from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkReviewBlocker, benchmarkReviewNotesMaxLength } from "@/features/benchmarks/models/BenchmarkModels";
import { isVerifiableCriterionKind, toBenchmarkCriterionKind } from "@/features/benchmarks/models/BenchmarkVerifier";

interface BenchmarkReviewFormProps {
	run: BenchmarkRunDetail;
	/**
	 * The project's CURRENT rubric, or null when the project has no judge policy — then the reviewer gives one overall
	 * 0..100 instead. Seeded once: the caller remounts the form (by `key`) when the run, its judging or its review moves.
	 */
	criteria: readonly BenchmarkRubricCriterion[] | null;
	isSaving?: boolean;
	onSubmit: (review: BenchmarkReviewSubmission) => void;
}

type Draft = Record<string, number | null>;

// Partial input ("", "-") is no score yet, never NaN or a silent 0.
const toScore = (value: string | number, max: number): number | null => {
	const parsed = Number(value);
	return value === "" || !Number.isFinite(parsed) ? null : Math.min(max, Math.max(0, Math.round(parsed)));
};

/**
 * The reviewer's decision on one scoring run of a review project. Only `llm` criteria are editable, pre-filled from the
 * stored review when there is one and from the judging otherwise; a verifiable criterion is shown with its verifier's
 * verdict and is never sent, because the node refuses a human overruling a deterministic check. Approve and Reject are
 * both locked until the run is reviewable (see `benchmarkReviewBlocker`), with the reason beside them.
 */
export function BenchmarkReviewForm({ run, criteria, isSaving = false, onSubmit }: BenchmarkReviewFormProps) {
	const { t } = useTranslation();
	const review = run.review;
	const llmCriteria = (criteria ?? []).filter(
		(criterion) => !isVerifiableCriterionKind(toBenchmarkCriterionKind(criterion.kind)),
	);
	const [draft, setDraft] = useState<Draft>(() => {
		const stored = new Map(review?.criteria.map((criterion) => [criterion.criterionId, criterion.score]));
		const judged = new Map(run.judge.criteria.map((criterion) => [criterion.id, criterion.score]));
		return Object.fromEntries(
			llmCriteria.map((criterion) => [criterion.id, stored.get(criterion.id) ?? judged.get(criterion.id) ?? null]),
		);
	});
	const [overall, setOverall] = useState<number | null>(review?.score ?? null);
	const [notes, setNotes] = useState(review?.notes ?? "");
	const verifiers = new Map(run.judge.verifiers.map((verifier) => [verifier.id, verifier]));
	const blocker = benchmarkReviewBlocker(run, criteria !== null);
	const locked = blocker !== null || isSaving;
	const complete =
		criteria === null ? overall !== null : llmCriteria.every((criterion) => typeof draft[criterion.id] === "number");
	const trimmedNotes = notes.trim().length > 0 ? notes.trim() : null;

	const approve = (): void =>
		onSubmit(
			criteria === null
				? { state: "approved", score: overall ?? 0, notes: trimmedNotes }
				: {
						state: "approved",
						criteria: llmCriteria.map((criterion) => ({ criterionId: criterion.id, score: draft[criterion.id] ?? 0 })),
						notes: trimmedNotes,
					},
		);

	return (
		<Card withBorder={true} radius="md" padding="md" data-testid="benchmark-review-form">
			<Stack gap="sm">
				<Group justify="space-between">
					<Group gap="xs">
						<IconUserCheck size={18} />
						<Title order={4} size="h5">
							{t("pages.benchmarks.review.title", "Review")}
						</Title>
					</Group>
					{review ? (
						<Group gap="xs" data-testid="benchmark-review-stored">
							<StatusBadge
								color={review.policyCurrent ? (review.state === "approved" ? "green" : "red") : "orange"}
								label={
									review.policyCurrent
										? t(`pages.benchmarks.review.states.${review.state}`, review.state)
										: t("pages.benchmarks.review.states.stale", "stale")
								}
							/>
							{review.score === null ? null : (
								<Text size="sm" fw={700}>
									{t("pages.benchmarks.review.storedScore", "{{score}} / 100", { score: review.score })}
								</Text>
							)}
							<Text size="xs" c="dimmed">
								{formatTimestamp(review.updatedAtUtc)}
							</Text>
						</Group>
					) : (
						<StatusBadge color="gray" label={t("pages.benchmarks.review.states.pending", "pending")} />
					)}
				</Group>
				{blocker === null ? null : (
					<Alert color="yellow" data-testid="benchmark-review-blocked">
						{t(`pages.benchmarks.review.blocked.${blocker}`, blocker)}
					</Alert>
				)}
				{criteria === null ? (
					<NumberInput
						label={t("pages.benchmarks.review.overall", "Overall score, 0 to 100")}
						min={0}
						max={100}
						clampBehavior="strict"
						allowNegative={false}
						allowDecimal={false}
						disabled={locked}
						value={overall ?? ""}
						onChange={(value) => setOverall(toScore(value, 100))}
						data-testid="benchmark-review-overall"
					/>
				) : (
					criteria.map((criterion) => {
						const kind = toBenchmarkCriterionKind(criterion.kind);
						if (isVerifiableCriterionKind(kind)) {
							const verifier = verifiers.get(criterion.id);
							return (
								<Stack key={criterion.id} gap={2} data-testid={`benchmark-review-verified-${criterion.id}`}>
									<Group gap="xs">
										<Text size="sm" fw={600}>
											{criterion.title || criterion.id}
										</Text>
										<StatusBadge
											color={verifier === undefined ? "gray" : verifier.passed ? "green" : "red"}
											label={
												verifier === undefined
													? t("pages.benchmarks.review.verifierMissing", "no verdict")
													: verifier.passed
														? t("pages.benchmarks.review.verifierPassed", "passed")
														: t("pages.benchmarks.review.verifierFailed", "failed")
											}
										/>
									</Group>
									<Text size="xs" c="dimmed">
										{verifier?.detail ??
											t("pages.benchmarks.review.verifierReadOnly", "Decided by its verifier; a review cannot change it.")}
									</Text>
								</Stack>
							);
						}
						return (
							<NumberInput
								key={criterion.id}
								label={criterion.title || criterion.id}
								description={t("pages.benchmarks.review.criterionHint", "0 to 10, pre-filled from the judging")}
								min={0}
								max={10}
								clampBehavior="strict"
								allowNegative={false}
								allowDecimal={false}
								disabled={locked}
								value={draft[criterion.id] ?? ""}
								onChange={(value) => setDraft((current) => ({ ...current, [criterion.id]: toScore(value, 10) }))}
								data-testid={`benchmark-review-criterion-${criterion.id}`}
							/>
						);
					})
				)}
				<Textarea
					label={t("pages.benchmarks.review.notes", "Notes")}
					autosize={true}
					minRows={2}
					maxLength={benchmarkReviewNotesMaxLength}
					disabled={locked}
					value={notes}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setNotes(value);
					}}
					data-testid="benchmark-review-notes"
				/>
				<Group gap="xs">
					<Button
						size="xs"
						leftSection={<IconCheck size={14} />}
						disabled={locked || !complete}
						loading={isSaving}
						onClick={approve}
						data-testid="benchmark-review-approve"
					>
						{t("pages.benchmarks.review.approve", "Approve")}
					</Button>
					<Button
						size="xs"
						variant="light"
						color="red"
						leftSection={<IconX size={14} />}
						disabled={locked}
						onClick={() => onSubmit({ state: "rejected", notes: trimmedNotes })}
						data-testid="benchmark-review-reject"
					>
						{t("pages.benchmarks.review.reject", "Reject")}
					</Button>
				</Group>
			</Stack>
		</Card>
	);
}
