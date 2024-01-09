// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.CSharp.Scripting;

namespace XMAT.Scripting
{
    public class ScriptHost
    {
        private Script _script;

        public Task<Diagnostic[]> CompileScriptAsync<T>(string script)
        {
            return Task.Run(() =>
            {
                Diagnostic[] finalArray = Array.Empty<Diagnostic>();

                if(!string.IsNullOrEmpty(script))
                {
                    // TODO: what other imports?  and/or can we add this via script?
                    Script<T> s = CSharpScript.Create<T>(script, ScriptOptions.Default.WithImports("System.Text.Json")
                                                                                      .WithReferences(typeof(JsonDocument).Assembly), typeof(ScriptGlobals<T>));
                    ImmutableArray<Diagnostic> diag = s.Compile();
                    if(diag != null && diag.Length > 0)
                    {
                        finalArray = new Diagnostic[diag.Length];
                        diag.CopyTo(finalArray);
                    }
                    else
                    {
                        _script = s;
                    }
                }
                return finalArray;
            });
        }

        public async Task<T> RunScriptAsync<T>(T input) where T : class
        {
            // if we didn't call Compile above, just bail out
            if(_script == null)
                return null;

            // run the script and return the params we passed in...
            // they are passed in byref it seems...
            await _script.RunAsync(new ScriptGlobals<T> { Params = input });
            return input;
        }
    }

    public class ScriptGlobals<T>
    {
        public T Params { get; set; }
    }
}
