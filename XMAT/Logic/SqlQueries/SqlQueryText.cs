// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using XMAT.SharedInterfaces;

namespace XMAT.SqlQueries
{
    internal sealed record SqlQueryCompletion(string Text, string Insertion, string Description);
    internal sealed record SqlQueryCompletionContext(
        int Start, int Length, string Prefix, IReadOnlyList<SqlQueryCompletion> Items);
    internal sealed record SqlQueryExecutionText(string Text, int EditorOffset);

    internal static class SqlQueryText
    {
        internal static readonly string[] Keywords =
        [
            "SELECT", "FROM", "WHERE", "JOIN", "LEFT", "INNER", "CROSS", "OUTER", "ON", "AS",
            "GROUP", "BY", "HAVING", "ORDER", "ASC", "DESC", "LIMIT", "OFFSET", "DISTINCT",
            "WITH", "RECURSIVE", "UNION", "ALL", "INTERSECT", "EXCEPT", "CASE", "WHEN",
            "THEN", "ELSE", "END", "AND", "OR", "NOT", "NULL", "IS", "IN", "EXISTS",
            "LIKE", "GLOB", "BETWEEN", "OVER", "PARTITION", "ROWS", "RANGE", "UNBOUNDED",
            "PRECEDING", "FOLLOWING", "CURRENT", "ROW", "FILTER", "COLLATE", "CAST"
        ];

        private static readonly string[] Functions =
        [
            "COUNT", "SUM", "AVG", "MIN", "MAX", "TOTAL", "COALESCE", "IFNULL", "NULLIF",
            "LENGTH", "SUBSTR", "INSTR", "LOWER", "UPPER", "TRIM", "REPLACE", "ROUND",
            "ABS", "TYPEOF", "HEX", "PRINTF", "DATE", "TIME", "DATETIME", "JULIANDAY",
            "STRFTIME", "ROW_NUMBER", "RANK", "DENSE_RANK", "LAG", "LEAD", "GROUP_CONCAT"
        ];

        internal static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        internal static SqlQueryExecutionText ExecutionText(string text, int selectionStart, int selectionLength)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentOutOfRangeException.ThrowIfNegative(selectionStart);
            ArgumentOutOfRangeException.ThrowIfNegative(selectionLength);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(selectionStart + (long)selectionLength, text.Length);
            return selectionLength == 0 ? new(text, 0) : new(text.Substring(selectionStart, selectionLength), selectionStart);
        }

        // SQLite offsets, when a provider supplies them, count UTF-8 bytes, not WPF UTF-16 characters.
        internal static int EditorOffset(string executedSql, int utf8Offset, int selectionOffset)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(utf8Offset);
            var bytes = 0;
            var characters = 0;
            foreach (var rune in executedSql.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > utf8Offset)
                {
                    break;
                }
                bytes += rune.Utf8SequenceLength;
                characters += rune.Utf16SequenceLength;
            }
            return selectionOffset + characters;
        }

        internal static SqlQueryCompletionContext Complete(string text, int caret, IReadOnlyList<QueryTable> schema)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentOutOfRangeException.ThrowIfNegative(caret);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(caret, text.Length);
            var tokens = Tokenize(text);
            var token = tokens.FirstOrDefault(item =>
                item.Start < caret && caret < item.End ||
                item.End == caret && (item.Kind is TokenKind.Word or TokenKind.Identifier || !item.Closed));
            if (token?.Kind is TokenKind.String or TokenKind.Comment)
            {
                return new(caret, 0, string.Empty, []);
            }
            var start = token?.Kind is TokenKind.Word or TokenKind.Identifier ? token.Start : caret;
            var end = token?.Kind is TokenKind.Word or TokenKind.Identifier ? token.End : caret;
            var prefix = text[start..caret];
            if (token?.Kind == TokenKind.Identifier)
            {
                prefix = Unquote(prefix, token.Closed && caret == token.End);
            }
            var preceding = tokens.Where(item => item.End <= start && item.Kind != TokenKind.Comment).ToArray();
            var qualifier = preceding.Length >= 2 && preceding[^1].Value == "."
                ? preceding[^2].Value : null;
            var aliases = new Dictionary<string, QueryTable>(StringComparer.OrdinalIgnoreCase);
            var meaningful = tokens.Where(item => item.Kind is not (TokenKind.Comment or TokenKind.String)).ToArray();
            for (var index = 0; index + 1 < meaningful.Length; index++)
            {
                if (!meaningful[index].Value.Equals("FROM", StringComparison.OrdinalIgnoreCase) &&
                    !meaningful[index].Value.Equals("JOIN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var nameIndex = index + 1;
                if (nameIndex + 2 < meaningful.Length && meaningful[nameIndex + 1].Value == ".")
                {
                    nameIndex += 2;
                }
                var table = schema.FirstOrDefault(item =>
                    item.Name.Equals(meaningful[nameIndex].Value, StringComparison.OrdinalIgnoreCase));
                if (table == null)
                {
                    continue;
                }
                aliases[table.Name] = table;
                var aliasIndex = nameIndex + 1;
                if (aliasIndex < meaningful.Length && meaningful[aliasIndex].Value.Equals("AS", StringComparison.OrdinalIgnoreCase))
                {
                    aliasIndex++;
                }
                if (aliasIndex < meaningful.Length &&
                    meaningful[aliasIndex].Kind is TokenKind.Word or TokenKind.Identifier &&
                    !Keywords.Contains(meaningful[aliasIndex].Value, StringComparer.OrdinalIgnoreCase))
                {
                    aliases[meaningful[aliasIndex].Value] = table;
                }
            }
            foreach (var table in schema)
            {
                aliases.TryAdd(table.Name, table);
            }
            var items = new List<SqlQueryCompletion>();
            if (qualifier != null)
            {
                if (aliases.TryGetValue(qualifier, out var table))
                {
                    AddColumns(items, table);
                }
            }
            else
            {
                if (token?.Kind != TokenKind.Identifier)
                {
                    items.AddRange(Keywords.Select(item => new SqlQueryCompletion(
                        item, item, Localization.GetLocalizedString("SQL_COMPLETION_KEYWORD"))));
                    items.AddRange(Functions.Select(item => new SqlQueryCompletion(
                        item, item + "(", Localization.GetLocalizedString("SQL_COMPLETION_FUNCTION"))));
                }
                foreach (var table in schema)
                {
                    items.Add(new(table.Name, QuoteIdentifier(table.Name), table.Kind));
                    AddColumns(items, table);
                }
                foreach (var alias in aliases.Where(item => !schema.Any(table => table.Name == item.Key)))
                {
                    foreach (var column in alias.Value.Columns)
                    {
                        items.Add(new(alias.Key + "." + column.Name,
                            QuoteIdentifier(alias.Key) + "." + QuoteIdentifier(column.Name), ColumnDescription(column)));
                    }
                }
            }
            return new(start, end - start, prefix, items
                .Where(item => item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(item => item.Insertion)
                .OrderBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }

        internal static string ColumnDescription(QuerySchemaColumn column) =>
            Localization.GetLocalizedString("SQL_SCHEMA_COLUMN_DESCRIPTION", column.DeclaredType,
                Localization.GetLocalizedString(column.PrimaryKey ? "SQL_PRIMARY_KEY" : "SQL_NOT_KEY"),
                Localization.GetLocalizedString(column.Nullable ? "SQL_NULLABLE" : "SQL_NOT_NULL"),
                column.Hidden ? Localization.GetLocalizedString("SQL_HIDDEN") : string.Empty);

        private static void AddColumns(List<SqlQueryCompletion> items, QueryTable table)
        {
            foreach (var column in table.Columns)
            {
                items.Add(new(column.Name, QuoteIdentifier(column.Name),
                    table.Name + " — " + ColumnDescription(column)));
            }
        }

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var index = 0;
            while (index < text.Length)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    index++;
                    continue;
                }
                var start = index;
                var character = text[index++];
                if (character == '-' && index < text.Length && text[index] == '-')
                {
                    while (index < text.Length && text[index] is not ('\r' or '\n'))
                        index++;
                    tokens.Add(new(start, index, text[start..index], TokenKind.Comment, index < text.Length));
                }
                else if (character == '/' && index < text.Length && text[index] == '*')
                {
                    index++;
                    while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/'))
                        index++;
                    var closed = index + 1 < text.Length;
                    index = closed ? index + 2 : text.Length;
                    tokens.Add(new(start, index, text[start..index], TokenKind.Comment, closed));
                }
                else if (character is '\'' or '"' or '`' or '[')
                {
                    var close = character == '[' ? ']' : character;
                    var closed = false;
                    while (index < text.Length)
                    {
                        if (text[index++] != close)
                            continue;
                        if (index < text.Length && text[index] == close && character != '[')
                        {
                            index++;
                            continue;
                        }
                        closed = true;
                        break;
                    }
                    var value = text[start..index];
                    tokens.Add(new(start, index,
                        character == '\'' ? value : Unquote(value, closed),
                        character == '\'' ? TokenKind.String : TokenKind.Identifier, closed));
                }
                else if (IsWord(character))
                {
                    while (index < text.Length && IsWord(text[index]))
                        index++;
                    tokens.Add(new(start, index, text[start..index], TokenKind.Word, true));
                }
                else
                {
                    tokens.Add(new(start, index, character.ToString(), TokenKind.Punctuation, true));
                }
            }
            return tokens;
        }

        private static string Unquote(string value, bool closed)
        {
            if (value.Length == 0)
                return value;
            var quote = value[0] == '[' ? ']' : value[0];
            var content = value.Substring(1, Math.Max(0, value.Length - (closed ? 2 : 1)));
            return content.Replace(new string(quote, 2), quote.ToString());
        }

        private static bool IsWord(char value) => char.IsLetterOrDigit(value) || value is '_' or '$';
        private enum TokenKind { Word, Identifier, String, Comment, Punctuation }
        private sealed record Token(int Start, int End, string Value, TokenKind Kind, bool Closed);
    }
}
