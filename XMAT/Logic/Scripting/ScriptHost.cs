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

        public Task<Diagnostic[]> CompileScriptAsync<T>(string script) =>
            CompileScriptAsync<T>(script, exactSourceOnly: false, CancellationToken.None);

        internal async Task<Diagnostic[]> CompileScriptAsync<T>(
            string script, bool exactSourceOnly, CancellationToken cancellationToken)
        {
            long compileVersion;
            lock (_syncRoot)
            {
                compileVersion = ++_compileVersion;
                _script = null;
            }

            (Script compiledScript, Diagnostic[] diagnostics) =
                await CompileCandidateAsync<T>(script, exactSourceOnly, cancellationToken).ConfigureAwait(false);

            lock (_syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (compileVersion == _compileVersion)
                    _script = compiledScript;
            }

            return diagnostics;
        }

        public async Task<Diagnostic[]> ValidateScriptAsync<T>(
            string script,
            CancellationToken cancellationToken = default)
        {
            var candidate = await CompileCandidateAsync<T>(script, exactSourceOnly: true, cancellationToken).ConfigureAwait(false);
            return candidate.Diagnostics;
        }

        private async Task<(Script Script, Diagnostic[] Diagnostics)> CompileCandidateAsync<T>(
            string script,
            bool exactSourceOnly,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_beforeCompileAsync != null)
                await _beforeCompileAsync(script).ConfigureAwait(false);

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(script))
                    return ((Script)null, Array.Empty<Diagnostic>());

                var options = ScriptOptions.Default.WithImports("System.Text.Json")
                    .WithReferences(typeof(JsonDocument).Assembly);
                if (exactSourceOnly)
                    options = options.WithSourceResolver(null).WithMetadataResolver(null);
                var candidate = CSharpScript.Create<T>(script, options, typeof(ScriptGlobals<T>));
                ImmutableArray<Diagnostic> diagnostics = candidate.Compile(cancellationToken);
                var installed = diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    ? null
                    : candidate;
                return ((Script)installed, diagnostics.ToArray());
            }, cancellationToken).ConfigureAwait(false);
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
