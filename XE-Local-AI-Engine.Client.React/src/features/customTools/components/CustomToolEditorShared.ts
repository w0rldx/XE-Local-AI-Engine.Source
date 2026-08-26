import { useCallback, useState } from "react";

import type { FieldErrors } from "@/features/customTools/models/CustomToolFormErrors";
import type { CustomToolFormValues } from "@/features/customTools/models/CustomToolModels";

let editableRowSequence = 0;
function nextEditableRowKey(): string {
	editableRowSequence += 1;
	return `custom-tool-row-${editableRowSequence}`;
}

export function useEditableRowKeys(initialLength: number) {
	const [rowKeys, setRowKeys] = useState(() => Array.from({ length: initialLength }, nextEditableRowKey));
	const appendRowKey = useCallback(() => setRowKeys((current) => [...current, nextEditableRowKey()]), []);
	const removeRowKey = useCallback(
		(index: number) => setRowKeys((current) => current.filter((_, position) => position !== index)),
		[],
	);
	return { rowKeys, appendRowKey, removeRowKey } as const;
}

export interface CustomToolEditorSectionProps {
	values: CustomToolFormValues;
	errors: FieldErrors;
	update: (updater: (current: CustomToolFormValues) => CustomToolFormValues) => void;
}
