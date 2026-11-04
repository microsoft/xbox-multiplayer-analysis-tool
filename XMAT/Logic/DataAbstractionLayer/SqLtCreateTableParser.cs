// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace XMAT.DataAbstractionLayer
{
    internal static class SqLtCreateTableParser
    {
        // Fail closed over every entry in SQLite version-3.50.3 tool/mkkeywordhash.c,
        // including WITHIN, whose ORDERSET mask is disabled in the bundled build.
        private static readonly HashSet<string> Keywords =
            new(
                new[]
                {
                    "ABORT", "ACTION", "ADD", "AFTER", "ALL", "ALTER", "ANALYZE", "AND", "AS", "ASC", "ATTACH",
                    "ALWAYS", "AUTOINCREMENT", "BEFORE", "BEGIN", "BETWEEN", "BY", "CASCADE", "CASE", "CAST", "CHECK",
                    "COLLATE",
                    "COLUMN", "COMMIT", "CONFLICT", "CONSTRAINT", "CREATE", "CROSS", "CURRENT", "CURRENT_DATE",
                    "CURRENT_TIME", "CURRENT_TIMESTAMP", "DATABASE", "DEFAULT", "DEFERRABLE", "DEFERRED", "DELETE",
                    "DESC", "DETACH", "DISTINCT", "DO", "DROP", "EACH", "ELSE", "END", "ESCAPE", "EXCEPT", "EXCLUDE",
                    "EXCLUSIVE", "EXISTS", "EXPLAIN", "FAIL", "FILTER", "FIRST", "FOLLOWING", "FOR", "FOREIGN", "FROM",
                    "FULL", "GENERATED", "GLOB", "GROUP", "GROUPS", "HAVING", "IF", "IGNORE", "IMMEDIATE", "IN", "INDEX",
                    "INDEXED", "INITIALLY", "INNER", "INSERT", "INSTEAD", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN",
                    "KEY", "LAST", "LEFT", "LIKE", "LIMIT", "MATCH", "MATERIALIZED", "NATURAL", "NO", "NOT", "NOTHING",
                    "NOTNULL", "NULL", "NULLS", "OF", "OFFSET", "ON", "OR", "ORDER", "OTHERS", "OUTER", "OVER",
                    "PARTITION", "PLAN", "PRAGMA", "PRECEDING", "PRIMARY", "QUERY", "RAISE", "RANGE", "RECURSIVE",
                    "REFERENCES", "REGEXP", "REINDEX", "RELEASE", "RENAME", "REPLACE", "RESTRICT", "RIGHT", "ROLLBACK",
                    "RETURNING", "ROW", "ROWS", "SAVEPOINT", "SELECT", "SET", "TABLE", "TEMP", "TEMPORARY", "THEN",
                    "TIES", "TO",
                    "TRANSACTION", "TRIGGER", "UNBOUNDED", "UNION", "UNIQUE", "UPDATE", "USING", "VACUUM", "VALUES",
                    "VIEW", "VIRTUAL", "WHEN", "WHERE", "WINDOW", "WITH", "WITHIN", "WITHOUT",
                },
                StringComparer.OrdinalIgnoreCase);

        internal static IReadOnlySet<string> RestrictedKeywords =>
            Keywords;

        internal static SqLtValidatedTable Parse(
            string ddl,
            string sqliteName,
            int rootPage,
            SqLtExpectedTable expected)
        {
            ArgumentNullException.ThrowIfNull(ddl);
            ArgumentNullException.ThrowIfNull(expected);
            var parser = new Parser(ddl);
            parser.ExpectKeyword("CREATE");
            parser.ExpectKeyword("TABLE");
            string parsedTableName = parser.ReadIdentifier();
            if (!parsedTableName.Equals(
                    sqliteName,
                    StringComparison.OrdinalIgnoreCase) ||
                !parsedTableName.Equals(
                    expected.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Unsupported();
            }

            parser.Expect(TokenKind.OpenParenthesis);
            string rowIdName = parser.ReadIdentifier();
            if (!rowIdName.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                throw Unsupported();
            }
            parser.ExpectKeyword("INTEGER");
            parser.ExpectKeyword("PRIMARY");
            parser.ExpectKeyword("KEY");

            var columns = new List<SqLtSchemaColumn>();
            var names = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                rowIdName,
            };
            while (parser.TryRead(TokenKind.Comma))
            {
                string name = parser.ReadIdentifier();
                if (!names.Add(name))
                {
                    throw Unsupported();
                }

                SqLtDeclaredType declaredType =
                    parser.TryReadKeyword("TEXT")
                        ? SqLtDeclaredType.Text
                        : parser.TryReadKeyword("INTEGER")
                            ? SqLtDeclaredType.Integer
                            : SqLtDeclaredType.None;
                if (!parser.IsNext(
                        TokenKind.Comma,
                        TokenKind.CloseParenthesis))
                {
                    throw Unsupported();
                }
                columns.Add(new SqLtSchemaColumn(
                    name,
                    declaredType));
            }

            parser.Expect(TokenKind.CloseParenthesis);
            _ = parser.TryRead(TokenKind.Semicolon);
            parser.Expect(TokenKind.End);

            if (columns.Count <
                expected.RequiredColumns.Count)
            {
                throw Unsupported();
            }
            if (columns.Count -
                expected.RequiredColumns.Count >
                SqLtSchemaProfile.MaxExtensionColumns)
            {
                throw Limit();
            }

            for (var index = 0;
                index < expected.RequiredColumns.Count;
                index++)
            {
                SqLtExpectedColumn required =
                    expected.RequiredColumns[index];
                SqLtSchemaColumn actual = columns[index];
                if (!actual.Name.Equals(
                        required.Name,
                        StringComparison.Ordinal) ||
                    actual.DeclaredType !=
                        required.DeclaredType)
                {
                    throw Unsupported();
                }
            }

            return new SqLtValidatedTable(
                expected.Name,
                sqliteName,
                rootPage,
                columns);
        }

        private static SqLtSchemaException Unsupported() =>
            new(SqLtSchemaFailure.Unsupported);

        private static SqLtSchemaException Limit() =>
            new(SqLtSchemaFailure.Limit);

        private enum TokenKind
        {
            Word,
            Identifier,
            OpenParenthesis,
            CloseParenthesis,
            Comma,
            Semicolon,
            End,
        }

        private readonly record struct Token(
            TokenKind Kind,
            string Text);

        private sealed class Parser
        {
            private readonly string _text;
            private int _position;
            private int _tokensRead;
            private Token? _lookahead;

            internal Parser(string text)
            {
                _text = text;
            }

            internal void ExpectKeyword(string keyword)
            {
                Token token = Read();
                if (token.Kind != TokenKind.Word ||
                    !token.Text.Equals(
                        keyword,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw Unsupported();
                }
            }

            internal bool TryReadKeyword(string keyword)
            {
                Token token = Peek();
                if (token.Kind != TokenKind.Word ||
                    !token.Text.Equals(
                        keyword,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                _ = Read();
                return true;
            }

            internal string ReadIdentifier()
            {
                Token token = Read();
                if (token.Kind is not (
                        TokenKind.Word or
                        TokenKind.Identifier) ||
                    !IsSimpleIdentifier(token.Text))
                {
                    throw Unsupported();
                }
                if (token.Text.Length >
                    SqLtSchemaProfile.MaxIdentifierCharacters)
                {
                    throw Limit();
                }
                if (token.Kind == TokenKind.Word &&
                    Keywords.Contains(token.Text))
                {
                    throw Unsupported();
                }
                return token.Text;
            }

            internal void Expect(TokenKind kind)
            {
                if (Read().Kind != kind)
                {
                    throw Unsupported();
                }
            }

            internal bool TryRead(TokenKind kind)
            {
                if (Peek().Kind != kind)
                {
                    return false;
                }
                _ = Read();
                return true;
            }

            internal bool IsNext(
                TokenKind first,
                TokenKind second)
            {
                TokenKind kind = Peek().Kind;
                return kind == first || kind == second;
            }

            private Token Peek()
            {
                _lookahead ??= ReadCore();
                return _lookahead.Value;
            }

            private Token Read()
            {
                Token token = _lookahead ??
                    ReadCore();
                _lookahead = null;
                return token;
            }

            private Token ReadCore()
            {
                if (++_tokensRead > 1024)
                {
                    throw Limit();
                }
                while (_position < _text.Length &&
                    IsSqliteWhitespace(_text[_position]))
                {
                    _position++;
                }
                if (_position == _text.Length)
                {
                    return new Token(
                        TokenKind.End,
                        string.Empty);
                }

                char current = _text[_position++];
                return current switch
                {
                    '(' => new Token(
                        TokenKind.OpenParenthesis,
                        "("),
                    ')' => new Token(
                        TokenKind.CloseParenthesis,
                        ")"),
                    ',' => new Token(
                        TokenKind.Comma,
                        ","),
                    ';' => new Token(
                        TokenKind.Semicolon,
                        ";"),
                    '"' => ReadQuoted(
                        '"',
                        '"'),
                    '`' => ReadQuoted(
                        '`',
                        '`'),
                    '[' => ReadQuoted(
                        ']',
                        ']'),
                    _ => ReadWord(current),
                };
            }

            private Token ReadWord(char first)
            {
                if (!IsIdentifierStart(first))
                {
                    throw Unsupported();
                }
                int start = _position - 1;
                while (_position < _text.Length &&
                    IsIdentifierPart(_text[_position]))
                {
                    _position++;
                }
                return new Token(
                    TokenKind.Word,
                    _text.Substring(
                        start,
                        _position - start));
            }

            private Token ReadQuoted(
                char terminator,
                char escape)
            {
                var value = new System.Text.StringBuilder();
                while (_position < _text.Length)
                {
                    char current = _text[_position++];
                    if (current == terminator)
                    {
                        if (_position < _text.Length &&
                            _text[_position] == escape)
                        {
                            value.Append(current);
                            _position++;
                            continue;
                        }
                        return new Token(
                            TokenKind.Identifier,
                            value.ToString());
                    }
                    value.Append(current);
                    if (value.Length >
                        SqLtSchemaProfile.MaxIdentifierCharacters)
                    {
                        throw Limit();
                    }
                }
                throw Unsupported();
            }

            private static bool IsSimpleIdentifier(string value)
            {
                if (string.IsNullOrEmpty(value) ||
                    !IsIdentifierStart(value[0]))
                {
                    return false;
                }
                for (var index = 1;
                    index < value.Length;
                    index++)
                {
                    if (!IsIdentifierPart(value[index]))
                    {
                        return false;
                    }
                }
                return true;
            }

            private static bool IsIdentifierStart(char value) =>
                value is >= 'A' and <= 'Z' or
                    >= 'a' and <= 'z' or
                    '_';

            private static bool IsIdentifierPart(char value) =>
                IsIdentifierStart(value) ||
                value is >= '0' and <= '9';

            private static bool IsSqliteWhitespace(char value) =>
                value is ' ' or '\t' or '\n' or '\f' or '\r';
        }
    }
}
