namespace GenProxy.Api.Host;

public static class Constants
{
    public static class API
    {
        public const string Title = "Gen Proxy Responses API";
        public const string Version = "v1";
    }

    public static class Auth
    {
        public const string ApiKeyHeaderName = "X-API-Key";
        public const string ApiKeyContextItemKey = "api_key";
    }
}
