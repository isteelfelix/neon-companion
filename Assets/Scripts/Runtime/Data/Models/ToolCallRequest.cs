using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class ToolCallRequest
    {
        public string id;           // unique ID for this request
        public string toolName;     // e.g. "read_file", "write_file", "execute_code"
        public string description;  // human-readable description
        public Dictionary<string, string> parameters; // tool parameters

        // Runtime state (not serialized)
        [NonSerialized] public bool alwaysApprove; // user chose "Always"
    }
}
