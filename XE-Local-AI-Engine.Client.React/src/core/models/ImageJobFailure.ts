import type { TFunction } from "i18next";

/** The failure fields every image-job shape carries: the REST response, the images view and the operations feed. */
interface ImageJobFailureFields {
	readonly failureReason?: string | null;
	readonly sanitizedError?: string | null;
}

/**
 * A failed image job's message in the operator's language. The stored English text is the fallback for a job with no
 * reason (failed before reasons were stored) or one this build has no text for.
 */
export function imageJobFailureMessage(t: TFunction, job: ImageJobFailureFields): string | null {
	const fallback = job.sanitizedError ?? null;
	return job.failureReason ? t(`pages.images.jobs.failure.${job.failureReason}`, fallback ?? "") : fallback;
}
