import { Button, type ButtonProps } from "@mantine/core";
import { createLink, type LinkComponent } from "@tanstack/react-router";
import { type AnchorHTMLAttributes, forwardRef } from "react";

type ButtonAnchorProps = ButtonProps & Omit<AnchorHTMLAttributes<HTMLAnchorElement>, keyof ButtonProps>;

const ButtonAnchor = forwardRef<HTMLAnchorElement, ButtonAnchorProps>((props, ref) => (
	<Button component="a" ref={ref} {...props} />
));

const CreatedButtonLink = createLink(ButtonAnchor);

// A Mantine Button that navigates like a router Link, with `to`, `params` and `search` type-checked against the route
// tree. `<Button component={Link}>` loses that typing, so a search param on it does not compile.
export const ButtonLink: LinkComponent<typeof ButtonAnchor> = (props) => <CreatedButtonLink {...props} />;
