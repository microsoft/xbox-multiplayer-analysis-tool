// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace CaptureAnalysisEngine
{
    public class ServiceCallStats
    {
        public UInt64 m_numCalls = 0;
        public double m_callEntropy = 0.0;
        public UInt64 m_lastReqTimeUTC = 0;
        public UInt64 m_avgTimeBetweenReqsMs = 0;
        public UInt64 m_varTimeBetweenReqsMs = 0;
        public UInt64 m_avgElapsedCallTimeMs = 0;
        public UInt64 m_varElapsedCallTimeMs = 0;
        public UInt64 m_maxElapsedCallTimeMs = 0;
        public UInt64 m_numSkippedCalls = 0;

        public string Stringify()
        {
            return string.Join(",",
                m_numCalls,
                m_callEntropy,
                m_lastReqTimeUTC,
                m_avgTimeBetweenReqsMs,
                m_varTimeBetweenReqsMs,
                m_avgElapsedCallTimeMs,
                m_varElapsedCallTimeMs,
                m_varElapsedCallTimeMs,
                m_maxElapsedCallTimeMs,
                m_numSkippedCalls
            );
        }

        public enum CallType
        {
            CallType_Get = 0,
            CallType_NonGet,
            CallType_Count
        };

        public Dictionary<UInt64 /*RequestBodyHash*/, UInt32 /*count*/> m_reqBodyHashCountMap = new Dictionary<UInt64 /*RequestBodyHash*/, UInt32 /*count*/>();

        public ServiceCallStats(IEnumerable<ServiceCallItem> data)
        {
            GatherStats(data);
        }

        public void GatherStats(IEnumerable<ServiceCallItem> data)
        {
            //create new Request Body Hash Count Map
            m_reqBodyHashCountMap = new Dictionary<UInt64, UInt32>();

            //Calculate 1st-order stats
            foreach (ServiceCallItem item in data)
            {
                GatherFirstOrderStats(item);
            }

            // Calculate 2nd-order stats (standard deviation)
            m_numCalls = 0;
            m_lastReqTimeUTC = 0;

            foreach (ServiceCallItem item in data)
            {
                GatherSecondOrderStats(item);
            }
        }

        private void GatherFirstOrderStats(ServiceCallItem item)
        {
            // Ignore shoulder taps
            if (item.IsShoulderTap)
            {
                return;
            }

            UInt64 n = m_numCalls;

            UInt64 avg = m_avgElapsedCallTimeMs;
            avg = n * avg + item.ElapsedCallTimeMs;
            avg /= (n + 1);

            //update values
            m_avgElapsedCallTimeMs = avg;

            //track skipped calls
            if (item.ReqTimeUTC < m_lastReqTimeUTC)
            {
                ++m_numSkippedCalls;
            }

            //m_avgTimeBetweenReqsMs
            if (m_lastReqTimeUTC != 0 && item.ReqTimeUTC >= m_lastReqTimeUTC)
            {
                UInt64 avgTime = m_avgTimeBetweenReqsMs;
                avgTime = n * avgTime + (item.ReqTimeUTC - m_lastReqTimeUTC) / TimeSpan.TicksPerMillisecond;
                avgTime /= (n + 1);
                m_avgTimeBetweenReqsMs = avgTime;
            }

            //update last call time
            m_lastReqTimeUTC = item.ReqTimeUTC;

            //increment num calls for next time
            ++m_numCalls;

            // Update m_maxElapsedCallTimeMs if applicable
            if (item.ElapsedCallTimeMs > m_maxElapsedCallTimeMs)
            {
                m_maxElapsedCallTimeMs = item.ElapsedCallTimeMs;
            }

            // m_reqBodyHashCountMap
            if (!m_reqBodyHashCountMap.ContainsKey(item.ReqBodyHash))
            {
                m_reqBodyHashCountMap.Add(item.ReqBodyHash, 1);
            }
            else
            {
                ++m_reqBodyHashCountMap[item.ReqBodyHash];
            }
        }

        private void GatherSecondOrderStats(ServiceCallItem item)
        {
            // Ignore shoulder taps
            if (item.IsShoulderTap)
            {
                return;
            }

            //
            // Calculate variance
            // 
            // Var[n+1] = ( n * Var[n] + ( x[n+1] - Avg ) ^ 2 ) / ( n + 1)
            //
            UInt64 avg = m_avgElapsedCallTimeMs;
            UInt64 n = m_numCalls;

            // m_varElapsedCallTimeMs
            UInt64 var = m_varElapsedCallTimeMs;
            UInt64 dev = item.ElapsedCallTimeMs - avg;
            var = n * var + dev * dev;
            var /= (n + 1);

            //m_varTimeBetweenReqsMs
            if (m_lastReqTimeUTC != 0 && item.ReqTimeUTC >= m_lastReqTimeUTC)
            {
                UInt64 localVar = m_varTimeBetweenReqsMs;
                UInt64 localDev = (item.ReqTimeUTC - m_lastReqTimeUTC) / TimeSpan.TicksPerMillisecond - avg;
                localVar = n * localVar + localDev * localDev;
                localVar /= (n + 1);
                //update values
                m_varTimeBetweenReqsMs = localVar;
            }

            m_lastReqTimeUTC = item.ReqTimeUTC;

            // increment m_numCalls for next time
            ++n;

            //update values
            m_numCalls = n;
            m_varElapsedCallTimeMs = var;
        }

        // TODO: remove unneeded call?
        //private void CalculateEntropy(String endpoint)
        //{
        //    {
        //        if (m_numCalls == 0)
        //        {
        //            return;
        //        }

        //        double entropy = 0.0f;

        //        foreach (UInt32 it in m_reqBodyHashCountMap.Values)
        //        {
        //            double p = ((double)it / (double)m_numCalls);
        //            entropy += p * Math.Log(1 / p) / Math.Log(2);
        //        }

        //        m_callEntropy = entropy;
        //    }
        //}
    }
}
