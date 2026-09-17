import { Anchor, Group, PasswordInput, Stack, Text, TextInput } from "@mantine/core";
import type { ChangeEvent, CSSProperties, ReactNode } from "react";
import { useTranslation } from "react-i18next";

interface StoredSecretInputProps {
	/** The caller's masking sentinel: the value that stands for "a secret is stored, keep it". */
	readonly sentinel: string;
	/**
	 * Whether the SERVER holds a secret for this field, derived by the parent from the data it was seeded with.
	 * It cannot be inferred here: a pending clear carries "", which is what a field that never had a value carries
	 * too. Remembering it inside this node instead was the bug — the memory died with every remount (a collapsed
	 * section, a switched editor), taking the "will be removed" warning with it while the "" stayed in the form.
	 */
	readonly stored: boolean;
	readonly value: string;
	readonly onChange: (value: string) => void;
	/** Masks what the operator types. An install dialog asking for an admin password must not print it on screen. */
	readonly masked?: boolean;
	/** Shown while a stored secret is kept; the caller owns the wording, so each feature keeps its own i18n namespace. */
	readonly storedPlaceholder: string;
	readonly placeholder?: string;
	readonly label?: ReactNode;
	/** Names the box for a caller that renders it without a visible label, e.g. one cell of a row editor. */
	readonly "aria-label"?: string;
	readonly description?: ReactNode;
	readonly error?: ReactNode;
	readonly withAsterisk?: boolean;
	readonly disabled?: boolean;
	readonly style?: CSSProperties;
	readonly "data-testid": string;
}

const keyPrefix = "components.storedSecretInput";

/**
 * The one place "keep this stored secret" and "clear it" are encoded. A stored secret arrives as the caller's
 * sentinel and its box renders EMPTY — the node never returns the value in clear — so an untouched save sends the
 * sentinel straight back and keeps it.
 *
 * Emptying the box does NOT clear: it returns to the sentinel. Clearing is the explicit action, which puts the row
 * into a visible "will be removed when you save" state carrying the empty string, the value both backends write over
 * the stored one. Before that, keep and clear differed only by an invisible sentinel: typing into a stored secret and
 * then deleting it destroyed the secret silently, and there was no deliberate way to remove one.
 */
export function StoredSecretInput({
	sentinel,
	stored,
	value,
	onChange,
	masked = false,
	storedPlaceholder,
	placeholder,
	label,
	"aria-label": ariaLabel,
	description,
	error,
	withAsterisk,
	disabled = false,
	style,
	"data-testid": testId,
}: StoredSecretInputProps) {
	const { t } = useTranslation();
	// Stateless on purpose: everything on screen is a function of the parent's `stored` and the current value, so the
	// node can be unmounted and rebuilt mid-edit without losing what a pending clear means. The sentinel is accepted as
	// evidence too, for a caller whose `stored` has not caught up with the value it is already showing.
	const hasStored = stored || value === sentinel;
	const cleared = hasStored && value.length === 0;
	const action = cleared ? "undo" : "clear";
	const inputProps = {
		label,
		"aria-label": ariaLabel,
		description,
		error,
		withAsterisk,
		disabled,
		placeholder: value === sentinel ? storedPlaceholder : placeholder,
		value: value === sentinel ? "" : value,
		onChange: (event: ChangeEvent<HTMLInputElement>) => {
			const next = event.currentTarget.value;
			// Emptying the box is "leave it alone", never "destroy it": only the explicit action below clears.
			onChange(next.length === 0 && hasStored ? sentinel : next);
		},
		"data-testid": testId,
	};

	return (
		<Stack gap={4} style={style} data-testid={`${testId}-column`}>
			{masked ? <PasswordInput {...inputProps} /> : <TextInput {...inputProps} />}
			{hasStored && !disabled ? (
				<Group gap="xs" align="center">
					{cleared ? (
						<Text size="xs" c="orange" data-testid={`${testId}-cleared`}>
							{t(`${keyPrefix}.cleared`)}
						</Text>
					) : null}
					<Anchor
						component="button"
						type="button"
						size="xs"
						onClick={() => onChange(cleared ? sentinel : "")}
						data-testid={`${testId}-${action}`}
					>
						{t(`${keyPrefix}.${action}`)}
					</Anchor>
				</Group>
			) : null}
		</Stack>
	);
}
