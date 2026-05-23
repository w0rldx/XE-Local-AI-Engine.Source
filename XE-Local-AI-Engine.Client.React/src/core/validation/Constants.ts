// Email Regex with additional # support
// This is required because external providers like Google may use # in the email address
export const EMAIL_REGEX =
	/^[\w!#$%&'*+./=?^`{|}~-]+@[\dA-Za-z]([\dA-Za-z-]{0,61}[\dA-Za-z])?(\.[\dA-Za-z]([\dA-Za-z-]{0,61}[\dA-Za-z])?)*$/;
