using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NeonCompanion.Runtime.Api.Hermes
{
    [Serializable]
    public class ModelOptionsResponse
    {
        [JsonProperty("model")] public string model;
        [JsonProperty("provider")] public string provider;
        [JsonProperty("providers")] public List<ModelOptionProvider> providers;
    }

    [Serializable]
    public class ModelOptionProvider
    {
        [JsonProperty("slug")] public string slug;
        [JsonProperty("name")] public string name;
        [JsonProperty("models")] public List<string> models;
        [JsonProperty("is_current")] public bool isCurrent;
        [JsonProperty("total_models")] public int totalModels;
        [JsonProperty("warning")] public string warning;
        [JsonProperty("unavailable_models")] public List<string> unavailableModels;
    }
}
