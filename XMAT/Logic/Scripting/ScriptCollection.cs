// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace XMAT.Scripting
{
    public class ScriptCollection : ObservableCollection<ScriptModel>
    {
        private readonly List<ScriptModel> _observedScripts = new();

        public ScriptCollection(Type t)
        {
            if (!t.IsEnum)
                throw new ApplicationException("Expecting an Enum type");

            // add an empty ScriptModel for every event we have
            Array vals = Enum.GetValues(t);

            foreach (var val in vals)
            {
                this.Add(new ScriptModel(val as Enum));
            }
        }

        public ScriptModel this[Enum e]
        {
            get => this[Convert.ToInt32(e)];
            set => this[Convert.ToInt32(e)] = value;
        }

        // Enabled scripts run on every proxied host, so the editor and its tab list them.
        public IReadOnlyList<ScriptModel> ActiveScripts => this.Where(IsActive).ToList();

        public bool HasActiveScripts => this.Any(IsActive);

        public string ActiveScriptsText => HasActiveScripts
            ? Localization.GetLocalizedString("SCRIPT_ACTIVE_LIST", string.Join(", ", ActiveScripts.Select(script => script.DisplayName)))
            : Localization.GetLocalizedString("SCRIPT_ACTIVE_NONE");

        public string ActiveCountText => HasActiveScripts
            ? Localization.GetLocalizedString("SCRIPT_TAB_ACTIVE_COUNT", ActiveScripts.Count)
            : string.Empty;

        public static string AllHostsWarningText => Localization.GetLocalizedString("SCRIPT_ALL_HOSTS_WARNING");

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            // Observe only the current models, so a replaced or removed one stops counting.
            foreach (var script in _observedScripts)
            {
                script.PropertyChanged -= Script_PropertyChanged;
            }

            _observedScripts.Clear();
            _observedScripts.AddRange(this.Where(script => script != null));
            foreach (var script in _observedScripts)
            {
                script.PropertyChanged += Script_PropertyChanged;
            }

            base.OnCollectionChanged(e);
            RaiseActiveScriptsChanged();
        }

        private static bool IsActive(ScriptModel script) => script?.IsEnabled == true;

        private void Script_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ScriptModel.IsEnabled))
            {
                RaiseActiveScriptsChanged();
            }
        }

        private void RaiseActiveScriptsChanged()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(ActiveScripts)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasActiveScripts)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(ActiveScriptsText)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(ActiveCountText)));
        }
    }
}
