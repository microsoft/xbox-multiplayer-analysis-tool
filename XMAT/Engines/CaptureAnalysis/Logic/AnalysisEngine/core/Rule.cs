// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CaptureAnalysisEngine
{
    public class RuleAttribute : Attribute {}

    public abstract class Rule
    {
        // Identifier for the type of rule
        public String RuleID { get; protected set; }

        // Identifier for the specific rule instance
        public String Name { get; set; }

        // Endpoint the rule will be analyzing
        public String Endpoint { get; set;  }

        // Base constructor that ensures a RuleID will exist.
        protected Rule()
        {
        }

        //  This method gets a JsonElement that has the data in the "Properties" object
        //  from the json description of the rule.
        //  {
        //      "Type": "{Class Name}",
        //      "Name": "{Rule Instance Name}",
        //      "Endpoint": "{Endpoint}", 
        //      "Properties":
        //      {  
        //          ...
        //      }
        //  }
        public abstract void DeserializeJson(JsonElement json);

        // TODO: get rid of serialization since it is maybe not really needed
        // unless we had some kind of exposed rule editor...
        // Fills in the JSON object properties section
        //public abstract Newtonsoft.Json.Linq.JObject SerializeJson();

        // Parameters:
        //   - items: List of the ServiceCallItems that describe calls to this rule's endpoint
        //   - stats: Simple statistics that were computed while this set of calls was being processed.
        public abstract RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats);

        // If the rule is created with a wildcard endpoint ("*") then it will be duplicated by the Rule Engine
        // to make an individual instance for each endpoint.  If there are internal data structures that need
        // to be deep copied, override this method.
        public virtual Rule Clone()
        {
            var clone = this.MemberwiseClone() as Rule;
            return clone;
        }

        // Creates a RuleResult with this Rule's Name and Endpoint and a custom description.
        protected RuleResult InitializeResult(String description)
        {
            RuleResult result = new RuleResult(Name, Endpoint, description);
            return result;
        }

        // Creates a RuleResult with a cleaner display veersion of the Rule's name, Endpoint and a custom description. 
        protected RuleResult InitializeResult(String displayName, String description)
        {
            RuleResult result = new RuleResult(displayName, Endpoint, description);
            return result;
        }
    }

    public abstract class BaseRule<T> : Rule
    {
        protected BaseRule() : base()
        {
            RuleID = typeof(T).Name;
        }
    }
}
