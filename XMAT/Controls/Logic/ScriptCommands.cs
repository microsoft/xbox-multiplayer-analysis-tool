// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Input;

namespace XMAT
{
    public static class ScriptCommands
    {
        public static readonly RoutedUICommand LoadScript =
            new(Localization.GetLocalizedString("SCRIPT_CMD_LOAD"), "Load Script", typeof(ScriptCommands));

        public static readonly RoutedUICommand SaveScript =
            new(Localization.GetLocalizedString("SCRIPT_CMD_SAVE"), "Save Script", typeof(ScriptCommands));

        public static readonly RoutedUICommand ValidateScript =
            new(Localization.GetLocalizedString("SCRIPT_CMD_VALIDATE"), "Validate Script", typeof(ScriptCommands));

        public static readonly RoutedUICommand RevertScript =
            new(Localization.GetLocalizedString("SCRIPT_CMD_REVERT"), "Revert Script", typeof(ScriptCommands));

        public static readonly RoutedUICommand ScriptErrorSelected =
            new(Localization.GetLocalizedString("SCRIPT_CMD_ERROR_SELECT"), "Script Error Selected", typeof(ScriptCommands));
    }
}
