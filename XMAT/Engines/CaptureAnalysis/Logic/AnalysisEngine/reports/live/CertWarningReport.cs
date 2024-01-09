// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CaptureAnalysisEngine
{
    public class CertWarningReportDocument : ReportDocument
    {
        internal CertWarningReportDocument()
        {
            WarningResults = new ();
        }

        public override string ToJson()
        {
            var options = new JsonSerializerOptions
            {
                IncludeFields = true,
            };
            return JsonSerializer.Serialize(
                this,
                this.GetType(),
                options);
        }

        public class CertWarning
        {
            public string XRName;
            public string Requirement;
            public string Remark;
            public string Intent;
        }

        public List<CertWarning> WarningResults { get; }
    }

    // TODO: if this is a deprecated report, it should not have the [Report] attribute
    [Report]
    public class CertWarningReport : Report
    {
        public CertWarningReport()
        {
        }

        public override ReportDocument RunReport(
            IEnumerable<RuleResult> result,
            Dictionary<string, Tuple<string, string, string>> endpoints)
        {
            var document = new CertWarningReportDocument();

            // XR-049: Rich Presence
            var x49results = result.Where(r => r.RuleName == "XR049Rule");
            bool x49violation = false;

            // If no results, treat as violation.
            if (x49results.Count() == 0)
            {
                x49violation = true;
            }
            else
            {
                var x49result = x49results.First();
                x49violation = x49result.ViolationCount > 0;
            }

            // Add XR 049 warning
            if (x49violation)
            {
                document.WarningResults.Add(
                    new CertWarningReportDocument.CertWarning
                    {
                        XRName = "XR-049: Rich Presence",
                        Requirement = "Games must update a user's presence information to accurately reflect his or her state.",
                        Remark = "The rich presence strings (including localized versions) must be configured in the Xbox service. Then titles must set rich presence strings by calling the SetPresenceAsync or set_presence API.  For more information about rich presence strings, see 'Rich Presence Strings Overview' in the Xbox One Development Kit or Xbox Application Development Kit documentation.",
                        Intent = "Provide up-to-date information regarding what users are doing in a title. It's a form of promotion for the title and promotes social interaction by giving other users context into what their friends are doing."
                    });
            }

            return document;
        }
    }
}
