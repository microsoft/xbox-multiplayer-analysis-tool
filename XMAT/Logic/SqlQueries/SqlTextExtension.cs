// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows.Markup;

namespace XMAT.SqlQueries
{
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class SqlTextExtension(string key) : MarkupExtension
    {
        [ConstructorArgument("key")]
        public string Key { get; set; } = key;
        public override object ProvideValue(IServiceProvider serviceProvider) => Localization.GetLocalizedString(Key);
    }
}
