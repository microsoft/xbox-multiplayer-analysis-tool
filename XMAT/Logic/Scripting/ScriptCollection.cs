// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.ObjectModel;

namespace XMAT.Scripting
{
    public class ScriptCollection : ObservableCollection<ScriptModel>
    {
        public ScriptCollection(Type t)
        {
            if(!t.IsEnum)
                throw new ApplicationException("Expecting an Enum type");

            // add an empty ScriptModel for every event we have
            Array vals = Enum.GetValues(t);

            foreach(var val in vals)
            {
                this.Add(new ScriptModel(val as Enum));
            }
        }

        public ScriptModel this[Enum e]
        {
            get => this[Convert.ToInt32(e)];
            set => this[Convert.ToInt32(e)] = value;
        }
    }
}
