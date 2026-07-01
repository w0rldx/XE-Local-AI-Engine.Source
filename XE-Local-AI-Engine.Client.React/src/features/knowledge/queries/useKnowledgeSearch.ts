import { useMutation } from "@tanstack/react-query";
import { useCallback, useState } from "react";

import { searchKnowledgeMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { KnowledgeSearchHit } from "@/features/knowledge/models/KnowledgeModels";

// Number of hits requested per search. Kept modest — the panel is a "does the KB know about X" probe, not a
// full retrieval UI. The backend re-clamps its own maximum.
const SEARCH_RESULT_LIMIT = 10;

export interface UseKnowledgeSearchResult {
	readonly results: readonly KnowledgeSearchHit[];
	readonly isSearching: boolean;
	readonly error: unknown;
	// True once at least one search has resolved this session — drives the empty-vs-no-results distinction.
	readonly hasSearched: boolean;
	// The query text of the most recently RESOLVED search (for the "no results for X" message).
	readonly lastQuery: string;
	search(query: string): void;
	reset(): void;
}

// Semantic search over the indexed corpus. Search is a POST (embedding the query server-side), so it rides a
// TanStack mutation rather than a cached query — each submit is an explicit, non-cached action. Results + the
// "has searched" flag live in local state so the panel can distinguish "nothing typed yet" from "searched, no
// hits". Response-shape failures surface as ApiError via withResponseValidation.
export function useKnowledgeSearch(): UseKnowledgeSearchResult {
	const [results, setResults] = useState<readonly KnowledgeSearchHit[]>([]);
	const [hasSearched, setHasSearched] = useState(false);
	const [lastQuery, setLastQuery] = useState("");

	const mutation = useMutation({
		...withResponseValidation(searchKnowledgeMutation()),
	});

	const { mutate } = mutation;

	const search = useCallback(
		(query: string): void => {
			const trimmed = query.trim();
			if (trimmed.length === 0) {
				return;
			}
			mutate(
				{ body: { query: trimmed, limit: SEARCH_RESULT_LIMIT } },
				{
					onSuccess: (response) => {
						setResults(response.results ?? []);
						setLastQuery(trimmed);
						setHasSearched(true);
					},
				},
			);
		},
		[mutate],
	);

	const reset = useCallback((): void => {
		mutation.reset();
		setResults([]);
		setHasSearched(false);
		setLastQuery("");
	}, [mutation]);

	return {
		results,
		isSearching: mutation.isPending,
		error: mutation.error,
		hasSearched,
		lastQuery,
		search,
		reset,
	};
}
