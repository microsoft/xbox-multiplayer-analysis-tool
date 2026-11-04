// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace XMAT.Scripting
{
    public class ScriptHost
    {
        private readonly Func<string, Task> _beforeCompileAsync;
        private readonly object _syncRoot = new();
        private Script _script;
        private long _compileVersion;

        public ScriptHost()
        {
        }

        internal ScriptHost(Func<string, Task> beforeCompileAsync)
        {
            _beforeCompileAsync = beforeCompileAsync;
        }

        public async Task<Diagnostic[]> CompileScriptAsync<T>(string script)
        {
            long compileVersion;
            lock (_syncRoot)
            {
                compileVersion = ++_compileVersion;
                _script = null;
            }

            if (_beforeCompileAsync != null)
                await _beforeCompileAsync(script).ConfigureAwait(false);

            (Script compiledScript, Diagnostic[] diagnostics) = await Task.Run(() =>
            {
                if (string.IsNullOrEmpty(script))
                    return (null, Array.Empty<Diagnostic>());

                // TODO: what other imports?  and/or can we add this via script?
                Script<T> candidate = CSharpScript.Create<T>(
                    script,
                    ScriptOptions.Default.WithImports("System.Text.Json")
                                         .WithReferences(typeof(JsonDocument).Assembly),
                    typeof(ScriptGlobals<T>));
                ImmutableArray<Diagnostic> candidateDiagnostics = candidate.Compile();
                var finalDiagnostics = new Diagnostic[candidateDiagnostics.Length];
                candidateDiagnostics.CopyTo(finalDiagnostics);

                Script installedScript = candidateDiagnostics.Any(
                    diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    ? null
                    : candidate;
                return (installedScript, finalDiagnostics);
            }).ConfigureAwait(false);

            lock (_syncRoot)
            {
                if (compileVersion == _compileVersion)
                    _script = compiledScript;
            }

            return diagnostics;
        }

        internal void ClearScript()
        {
            lock (_syncRoot)
            {
                ++_compileVersion;
                _script = null;
            }
        }

        public async Task<T> RunScriptAsync<T>(
            T input,
            CancellationToken cancellationToken = default) where T : class
        {
            Script script;
            lock (_syncRoot)
            {
                script = _script;
            }

            if (script == null)
                return null;

            // run the script and return the params we passed in...
            // they are passed in byref it seems...
            await script.RunAsync(
                new ScriptGlobals<T> { Params = input },
                cancellationToken: cancellationToken);
            return input;
        }
    }

    public class ScriptGlobals<T>
    {
        public T Params { get; set; }
    }
}
