using System;

namespace NeonCompanion.Runtime.Api.Models
{
    /// <summary>
    /// Serializable subset of a Responses API response. The fields deliberately cover
    /// every output item that must survive a later conversation continuation.
    /// </summary>
    [Serializable]
    public class ResponsesApiResponse
    {
        public string id;
        public string @object;
        public string model;
        public string status;
        public ResponsesOutputItem[] output;
        public ResponsesUsage usage;
        public ResponsesApiError error;
        public ResponsesIncompleteDetails incomplete_details;
    }

    [Serializable]
    public class ResponsesOutputItem
    {
        public string id;
        public string type;
        public string status;
        public string role;
        public string call_id;
        public string name;
        public string arguments;
        public string encrypted_content;
        public ResponsesContentPart[] content;
        public ResponsesContentPart[] summary;
    }

    [Serializable]
    public class ResponsesContentPart
    {
        public string type;
        public string text;
        public string refusal;
    }

    [Serializable]
    public class ResponsesUsage
    {
        public int input_tokens;
        public int output_tokens;
        public int total_tokens;
        public ResponsesInputTokenDetails input_tokens_details;
        public ResponsesOutputTokenDetails output_tokens_details;
    }

    [Serializable]
    public class ResponsesInputTokenDetails
    {
        public int cached_tokens;
    }

    [Serializable]
    public class ResponsesOutputTokenDetails
    {
        public int reasoning_tokens;
    }

    [Serializable]
    public class ResponsesApiError
    {
        public string code;
        public string message;
        public string param;
        public string type;
    }

    [Serializable]
    public class ResponsesIncompleteDetails
    {
        public string reason;
    }

    [Serializable]
    public class ResponsesStreamEvent
    {
        public string type;
        public string delta;
        public string item_id;
        public int output_index;
        public int content_index;
        public int sequence_number;
        public ResponsesApiResponse response;
        public ResponsesOutputItem item;
        public ResponsesContentPart part;
        public ResponsesApiError error;
        public string code;
        public string message;
        public string param;
        public string call_id;
        public string name;
        public string arguments;
    }
}
