import i18next from "i18next";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { FieldWithInfo } from "@/core/ui/components/FieldInfo/FieldInfo";
import {
	nodeSettingsFieldKey,
	nodeSettingsFieldLabelText,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";

// Wraps one field's input with its help card at the end of the label line, outside the <label> so the restart badge
// stays in the input's accessible name and the text stays out of it. A field shows the (i) as soon as the bundles hold
// `pages.nodeSettings.fields.<field>.info`; without that text the input renders alone.
export function NodeSettingsFieldWithInfo({ field, children }: { readonly field: string; readonly children: ReactNode }) {
	const { t } = useTranslation();
	const infoKey = `${nodeSettingsFieldKey(field)}.info`;
	return (
		<FieldWithInfo
			label={nodeSettingsFieldLabelText(t, field)}
			text={i18next.exists(infoKey) ? t(infoKey) : undefined}
			testId={`node-settings-info-${field}`}
		>
			{children}
		</FieldWithInfo>
	);
}
