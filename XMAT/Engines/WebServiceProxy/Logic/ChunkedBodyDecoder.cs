// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;

namespace XMAT.WebServiceCapture
{
    internal static class ChunkedBodyDecoder
    {
        internal static byte[] Decode(byte[] body, int maximumBodyBytes, Action<int> spendWork = null)
        {
            using var output = new MemoryStream();
            int position = 0;
            while (true)
            {
                spendWork?.Invoke(1);
                string line = ReadLine();
                int extension = line.IndexOf(';');
                string sizeText = extension < 0 ? line : line[..extension].TrimEnd(' ', '\t');
                if (sizeText.Length == 0 || !sizeText.All(Uri.IsHexDigit) ||
                    !int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int size) ||
                    size < 0 ||
                    (extension >= 0 && !TransferCodingHeaderValue.TryParse("chunked" + line[extension..], out _)))
                {
                    throw new InvalidDataException("Invalid chunk size or extension.");
                }
                if (size == 0)
                {
                    while (true)
                    {
                        spendWork?.Invoke(1);
                        string trailer = ReadLine();
                        if (trailer.Length == 0)
                        {
                            if (position != body.Length)
                                throw new InvalidDataException("Unexpected bytes after the final chunk.");
                            return output.ToArray();
                        }
                        int separator = trailer.IndexOf(':');
                        if (separator <= 0 ||
                            !trailer[..separator].All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)) ||
                            trailer[(separator + 1)..].Any(c => (char.IsControl(c) && c != '\t') || c == '\u007f'))
                        {
                            throw new InvalidDataException("Invalid chunk trailer.");
                        }
                    }
                }
                if (size > maximumBodyBytes - output.Length || size > body.Length - position - 2 ||
                    body[position + size] != 13 || body[position + size + 1] != 10)
                {
                    throw new InvalidDataException("Incomplete chunk data or missing delimiter.");
                }
                spendWork?.Invoke(size);
                output.Write(body, position, size);
                position += size + 2;
            }

            string ReadLine()
            {
                int lineEnd = body.AsSpan(position).IndexOf("\r\n"u8);
                if (lineEnd < 0 || lineEnd > 8192)
                    throw new InvalidDataException("Invalid or incomplete chunk line.");
                string line = Encoding.Latin1.GetString(body, position, lineEnd);
                position += lineEnd + 2;
                return line;
            }
        }
    }
}
