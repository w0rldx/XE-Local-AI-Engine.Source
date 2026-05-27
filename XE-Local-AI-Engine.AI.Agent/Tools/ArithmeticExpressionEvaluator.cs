namespace XE_Local_AI_Engine.AI.Agent.Tools;

using System.Globalization;

/// <summary>
/// Safe recursive-descent evaluator for basic arithmetic expressions (<c>+ - * / ( )</c>, unary minus, decimals).
/// Deliberately NOT a general expression engine: there is no identifier, function-call, or code-execution path, so
/// there is nothing for a model to abuse. Any character outside the arithmetic alphabet is rejected up front.
/// </summary>
internal static class ArithmeticExpressionEvaluator
{
    /// <summary>
    /// Attempts to evaluate <paramref name="expression"/>. Returns <see langword="false"/> for empty, non-arithmetic,
    /// malformed, or non-finite (overflow / divide-by-zero) input rather than throwing.
    /// </summary>
    public static bool TryEvaluate(string? expression, out double result)
    {
        result = 0;

        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        if (expression.Any(static character => !IsAllowedCharacter(character)))
        {
            return false;
        }

        var parser = new Parser(expression);
        if (!parser.TryParseExpression(out var value))
        {
            return false;
        }

        if (!parser.IsAtEnd)
        {
            return false;
        }

        if (!double.IsFinite(value))
        {
            return false;
        }

        result = value;
        return true;
    }

    private static bool IsAllowedCharacter(char character)
    {
        return char.IsAsciiDigit(character)
               || character is '+' or '-' or '*' or '/' or '(' or ')' or '.'
               || char.IsWhiteSpace(character);
    }

    private struct Parser
    {
        private readonly string _text;
        private int _position;

        public Parser(string text)
        {
            _text = text;
            _position = 0;
        }

        public readonly bool IsAtEnd
        {
            get
            {
                var index = _position;
                while (index < _text.Length && char.IsWhiteSpace(_text[index]))
                {
                    index++;
                }

                return index >= _text.Length;
            }
        }

        // expression := term (('+' | '-') term)*
        public bool TryParseExpression(out double value)
        {
            if (!TryParseTerm(out value))
            {
                return false;
            }

            while (TryPeekOperator(out var op) && op is '+' or '-')
            {
                _position++;
                if (!TryParseTerm(out var right))
                {
                    return false;
                }

                value = op == '+' ? value + right : value - right;
            }

            return true;
        }

        // term := factor (('*' | '/') factor)*
        private bool TryParseTerm(out double value)
        {
            if (!TryParseFactor(out value))
            {
                return false;
            }

            while (TryPeekOperator(out var op) && op is '*' or '/')
            {
                _position++;
                if (!TryParseFactor(out var right))
                {
                    return false;
                }

                // Division by zero yields a non-finite result, which the top-level IsFinite guard rejects, so there
                // is no exact-zero comparison here (and no S1244 floating-point-equality smell).
                value = op == '*' ? value * right : value / right;
            }

            return true;
        }

        // factor := ('+' | '-') factor | '(' expression ')' | number
        private bool TryParseFactor(out double value)
        {
            value = 0;
            SkipWhitespace();

            if (_position >= _text.Length)
            {
                return false;
            }

            var current = _text[_position];
            if (current is '+' or '-')
            {
                _position++;
                if (!TryParseFactor(out var inner))
                {
                    return false;
                }

                value = current == '-' ? -inner : inner;
                return true;
            }

            if (current == '(')
            {
                _position++;
                if (!TryParseExpression(out value))
                {
                    return false;
                }

                SkipWhitespace();
                if (_position >= _text.Length || _text[_position] != ')')
                {
                    return false;
                }

                _position++;
                return true;
            }

            return TryParseNumber(out value);
        }

        private bool TryParseNumber(out double value)
        {
            value = 0;
            SkipWhitespace();

            var start = _position;
            var sawDigit = false;
            var sawDot = false;

            while (_position < _text.Length)
            {
                var current = _text[_position];
                if (char.IsAsciiDigit(current))
                {
                    sawDigit = true;
                    _position++;
                }
                else if (current == '.' && !sawDot)
                {
                    sawDot = true;
                    _position++;
                }
                else
                {
                    break;
                }
            }

            if (!sawDigit)
            {
                return false;
            }

            var slice = _text.AsSpan(start, _position - start);
            return double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private bool TryPeekOperator(out char op)
        {
            op = '\0';
            SkipWhitespace();
            if (_position >= _text.Length)
            {
                return false;
            }

            op = _text[_position];
            return op is '+' or '-' or '*' or '/';
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }
    }
}
