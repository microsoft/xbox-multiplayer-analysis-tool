// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public sealed class SqLtWebProxySchemaTests
    {
        [Fact]
        public void WritableWebProxyTableAppendsTypedOptionalColumns()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            var app = new TestAppModel(database);
            var method = (WebServiceCaptureMethod) RuntimeHelpers.GetUninitializedObject(
                typeof(WebServiceCaptureMethod));
            typeof(WebServiceCaptureMethod).GetField("_captureAppModel", BindingFlags.Instance |
                BindingFlags.NonPublic).SetValue(method, app);

            typeof(WebServiceCaptureMethod).GetMethod("InitializeDataTables", BindingFlags.Instance |
                BindingFlags.NonPublic).Invoke(method, null);

            IFieldDefinition[] fields = method.WebProxyConnectionsTable.Dataset.FieldDefs.ToArray();
            Assert.Equal(22, fields.Length);
            Assert.Collection(
                fields.Skip(16),
                field => AssertField(field, WebProxyCaptureSchema.ClientProcessName, FieldValueType.String),
                field => AssertField(field, WebProxyCaptureSchema.ClientProcessId, FieldValueType.Integer),
                field => AssertField(field, WebProxyCaptureSchema.ClientProcessStartTime, FieldValueType.String),
                field => AssertField(field, WebProxyCaptureSchema.ClientProcessStatus, FieldValueType.String),
                field => AssertField(field, WebProxyCaptureSchema.ResponseTrailers, FieldValueType.String),
                field => AssertField(field, WebProxyCaptureSchema.ResponseBodyTruncatedAt, FieldValueType.Integer));
        }

        private static void AssertField(IFieldDefinition field, string name, FieldValueType type)
        {
            Assert.Equal(name, field.Name);
            Assert.Equal(type, field.ValueType);
        }

        private sealed class TestAppModel(IDatabase database) : ICaptureAppModel
        {
            public IDatabase ActiveDatabase { get; } = database;
            public IReadonlyDatabase LoadedDatabase => null;
            public ObservableCollection<ICaptureDeviceContext> CaptureDeviceContexts { get; } = new();
            public ObservableCollection<ICaptureAnalysisRun> AnalysisRuns { get; } = new();
        }
    }
}
