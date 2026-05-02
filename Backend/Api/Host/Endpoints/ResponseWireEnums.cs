using System.Text.Json.Serialization;

namespace GenProxy.Api.Host.Endpoints;

[JsonConverter(typeof(JsonStringEnumConverter<ResponseObjectType>))]
public enum ResponseObjectType
{
    [JsonStringEnumMemberName("response")]
    Response = 0,
}

[JsonConverter(typeof(JsonStringEnumConverter<ResponseStatus>))]
public enum ResponseStatus
{
    [JsonStringEnumMemberName("completed")]
    Completed = 0,
}

[JsonConverter(typeof(JsonStringEnumConverter<ResponseItemType>))]
public enum ResponseItemType
{
    [JsonStringEnumMemberName("message")]
    Message = 0,
}

[JsonConverter(typeof(JsonStringEnumConverter<ResponseRole>))]
public enum ResponseRole
{
    [JsonStringEnumMemberName("user")]
    User = 0,
    [JsonStringEnumMemberName("assistant")]
    Assistant = 1,
    [JsonStringEnumMemberName("system")]
    System = 2,
    [JsonStringEnumMemberName("developer")]
    Developer = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<ResponseContentPartType>))]
public enum ResponseContentPartType
{
    [JsonStringEnumMemberName("text")]
    Text = 0,
    [JsonStringEnumMemberName("input_text")]
    InputText = 1,
    [JsonStringEnumMemberName("output_text")]
    OutputText = 2,
}
