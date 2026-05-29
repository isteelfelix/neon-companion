using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Api.Models
{
    [Serializable]
    public class ToolDefinition
    {
        public string name;
        public string description;
        public ToolParameterSchema parameters;
    }

    [Serializable]
    public class ToolParameterSchema
    {
        public string type = "object";
        public Dictionary<string, ToolParameterProperty> properties = new Dictionary<string, ToolParameterProperty>();
        public List<string> required = new List<string>();
    }

    [Serializable]
    public class ToolParameterProperty
    {
        public string type;
        public string description;
    }
}
