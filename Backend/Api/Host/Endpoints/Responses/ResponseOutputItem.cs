namespace GenProxy.Api.Host.Endpoints;

public record ResponseOutputItem(string Id, string Type, string Role, IReadOnlyList<ResponseContentPart> Content);
