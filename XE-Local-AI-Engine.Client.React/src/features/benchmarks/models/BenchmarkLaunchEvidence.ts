import { formatBytesAsGb } from "@/core/formatting/BytesFormatting";
import type { BenchmarkEvidenceObject, BenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";

// Launch evidence is rendered and compared field-by-field without the frontend knowing the field set: the node records
// facts rather than verdicts, and a contract addition must show up in the detail blocks and compare diff without a UI
// change.
// So everything below walks the decoded objects generically instead of naming their members.

export interface BenchmarkEvidenceEntry {
	/** Dotted path, e.g. `receipt.launchProjection.kvCacheTypeK`. Field paths are wire identifiers, not prose. */
	key: string;
	value: unknown;
}

/**
 * One field across every compared run, in the order the runs were given. `values` is always as long as the compared
 * set — a run that never recorded the field contributes `null` rather than a hole, so column N of the table is always
 * run N.
 */
export interface BenchmarkEvidenceDiffRow {
	key: string;
	values: readonly unknown[];
	differs: boolean;
}

const isPlainObject = (value: unknown): value is Record<string, unknown> =>
	typeof value === "object" && value !== null && !Array.isArray(value);

/** Depth-first leaf list. Arrays flatten by index; an empty object/array stays as one absent-valued leaf. */
export function flattenEvidence(value: unknown, prefix: string): BenchmarkEvidenceEntry[] {
	if (Array.isArray(value)) {
		return value.length > 0
			? value.flatMap((item, index) => flattenEvidence(item, `${prefix}.${index}`))
			: [{ key: prefix, value: null }];
	}
	if (isPlainObject(value)) {
		const members = Object.entries(value);
		return members.length > 0
			? members.flatMap(([key, member]) => flattenEvidence(member, `${prefix}.${key}`))
			: [{ key: prefix, value: null }];
	}
	return [{ key: prefix, value: value ?? null }];
}

/**
 * Every comparable fact of one launch side: the flat columns first (they carry the hashes and identities), then the
 * decoded receipt and environment objects.
 */
export function launchEvidenceEntries(
	launch: BenchmarkLaunchFacts,
	receipt: BenchmarkEvidenceObject | null,
	environment: BenchmarkEvidenceObject | null,
): BenchmarkEvidenceEntry[] {
	return [
		...Object.entries(launch).map(([key, value]) => ({ key: `launch.${key}`, value })),
		...(receipt === null ? [] : flattenEvidence(receipt, "receipt")),
		...(environment === null ? [] : flattenEvidence(environment, "environment")),
	];
}

const lastSegment = (key: string): string => key.split(".").at(-1) ?? key;

const toMap = (entries: readonly BenchmarkEvidenceEntry[]): Map<string, unknown> =>
	new Map(entries.map((entry) => [entry.key, entry.value]));

// Stamped per capture by construction, so it can never match across two runs: comparing it would report every pair of
// runs as differing. The row is still rendered with both values — it just does not count as a difference.
const isInformationalEvidenceKey = (key: string): boolean => lastSegment(key) === "capturedAtUtc";

/**
 * Union of every side's fields, in first-seen order, each row flagged when the sides do not all agree. Informational
 * fields never flag, so callers deriving a banner from `differs` inherit the exclusion.
 *
 * N-ary rather than pairwise because the operator compares a model's quants, not two runs — and a pairwise engine
 * generalized by running it N-1 times would report "differs" against whichever run happened to be first rather than
 * against the set. One side in is a valid degenerate case: every row renders, nothing differs.
 */
export function diffLaunchEvidence(sides: readonly (readonly BenchmarkEvidenceEntry[])[]): BenchmarkEvidenceDiffRow[] {
	const maps = sides.map(toMap);
	const keys: string[] = [];
	const seen = new Set<string>();
	for (const map of maps) {
		for (const key of map.keys()) {
			if (!seen.has(key)) {
				seen.add(key);
				keys.push(key);
			}
		}
	}
	return keys.map((key) => {
		const values = maps.map((map) => map.get(key) ?? null);
		// Compared against the FIRST side rather than pairwise across all of them: `Object.is` is transitive here
		// because every value is a primitive leaf, so "some value differs from the first" is exactly "not all equal".
		const differs = !isInformationalEvidenceKey(key) && values.some((value) => !Object.is(value, values[0] ?? null));
		return { key, values, differs };
	});
}

export const differingEvidenceKeys = (rows: readonly BenchmarkEvidenceDiffRow[]): string[] =>
	rows.filter((row) => row.differs).map((row) => row.key);

const hashSegment = /(sha256|hash|identity|fingerprint)$/i;

/** Hash-like fields are truncated for display; the untruncated value stays available to copy. */
export const isEvidenceHashKey = (key: string): boolean => hashSegment.test(lastSegment(key));

/**
 * @param truncateHashes Pass false where two values are shown side by side and known to differ: two different hashes
 * sharing a 12-character prefix render as the SAME truncated string, which reads as a table flagging identical values.
 */
export function formatEvidenceValue(key: string, value: unknown, truncateHashes = true): string {
	if (value === null || value === undefined || value === "") {
		return "—";
	}
	if (typeof value === "number" && lastSegment(key).endsWith("Bytes")) {
		// RAM and VRAM are GB-scale but the runtime-bundle file list is not, and a small file is not "0.0 GB": pick the
		// unit from the magnitude so every recorded size stays readable as itself.
		if (value >= 1024 ** 3) {
			return formatBytesAsGb(value);
		}
		return value >= 1024 ** 2 ? `${(value / 1024 ** 2).toFixed(1)} MB` : `${value} B`;
	}
	const rendered = String(value);
	return truncateHashes && isEvidenceHashKey(key) && rendered.length > 20 ? `${rendered.slice(0, 12)}…` : rendered;
}
