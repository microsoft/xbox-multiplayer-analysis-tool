// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text;

namespace CaptureAnalysisEngine
{
    public enum ViolationLevel
    {
        Warning,
        Error,
        Info
    };

    // When a rule detects an issue, that issue is logged into a violation that will be stored in the RuleResult.
    public class RuleViolation
    {
        public ViolationLevel m_level = ViolationLevel.Warning;
        public String m_endpoint = String.Empty;
        public String m_summary = String.Empty;
        public List<ServiceCallItem> m_violatingCalls = new List<ServiceCallItem>();

        public override String ToString()
        {
            if (m_violatingCalls.Count > 0)
            {
                StringBuilder idRange = new StringBuilder();
                Utils.PrintCallIdRange(idRange, m_violatingCalls, 10);
                return String.Format("[{0}][{1}] {2} ID(s): {3}", System.Enum.GetName(m_level.GetType(), m_level), m_endpoint, m_summary, idRange.ToString());
            }
            else
            {
                return String.Format("[{0}][{1}] {2}", System.Enum.GetName(m_level.GetType(), m_level), m_endpoint, m_summary);
            }
        }
    }
}
