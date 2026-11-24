import { useNavigate, useRouter, useSearch } from "@tanstack/react-router";
import { useCallback } from "react";

import { applyModelLabPatch, type ModelLabUpdate } from "@/features/modelLab/models/ModelLabNavigation";
import { ModelLabPage } from "@/features/modelLab/pages/ModelLabPage";

// The router adapter: ModelLabPage stays router-free for unit tests, so the URL's search is read here and handed down.
export function ModelLabRoutePage() {
	const search = useSearch({ from: "/_layout/model-lab" });
	const navigate = useNavigate({ from: "/model-lab" });
	const router = useRouter();

	const handleSearchChange = useCallback(
		(update: ModelLabUpdate, push: boolean, onShown?: () => void) => {
			// A change that lands after the operator left (a delete's late answer) must not bring Model Lab back. While a
			// navigation loads, the location it goes to counts, not the one still shown.
			if (!router.matchRoute({ to: "/model-lab" }, { pending: router.state.status === "pending" })) {
				return;
			}
			// A blocked navigation's promise stays open until a later one settles it, so the entry this change made is
			// recognised by its own mark in the history state. The router's own keys are kept and renewed by the push.
			const mark = onShown ? crypto.randomUUID() : undefined;
			// Only a jump between tabs pushes, so Back undoes a jump and otherwise leaves the page. Nothing here moves the
			// scroller. Best-effort: a failed search update must not break the page.
			navigate({
				search: (previous) => applyModelLabPatch(previous, update),
				replace: !push,
				resetScroll: false,
				...(mark && { state: (previous) => ({ ...previous, modelLabChange: mark }) }),
			}).then(
				() => {
					const shown = router.state.location.state;
					if (mark && "modelLabChange" in shown && shown.modelLabChange === mark) {
						onShown?.();
					}
				},
				() => undefined,
			);
		},
		[navigate, router],
	);

	return <ModelLabPage search={search} onSearchChange={handleSearchChange} />;
}
