// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace CaptureAnalysisEngine
{
    public class ReportAttribute : Attribute { }

    public abstract class ReportDocument
    {
        public abstract string ToJson();
    }

    // Interface for reporting the results of the analysis
    public abstract class Report
    {
        // Parameters:
        //  - results:          IEnumerable of all of the results from the Rules
        //  - endpointMap:      A mapping of all of the Xbox Live endpoint to the XSAPI method.
        // return value:        A native document data structure for the report that is
        //  convertible to JSON text.
        public abstract ReportDocument RunReport(
            IEnumerable<RuleResult> results,
            Dictionary<String, Tuple<String, String, String>> endpointsMap);
    }
}
