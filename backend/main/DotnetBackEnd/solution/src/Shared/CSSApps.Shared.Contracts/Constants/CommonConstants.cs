namespace CSSApps.Shared.Contracts.Constants;

public static class CommonConstants
{
    public abstract class EntityStatusType
    {
        public const string Active = "ACTIVE";
        public const string Inactive = "INACTIVE";
        public const string Deleted = "DELETED";
    }

    public abstract class Headers
    {
        public const string CorrelationHeaderKey = "X-Correlation-ID";
        public const string RequestIdHeaderKey = "X-Request-ID";
        public const string ApiKeyHeaderKey = "X-Api-Key";
        public const string ApiVersionKey = "X-Version";
        public const string ForwardedHostHeaderKey = "X-Forwarded-Host";
        public const string ForwardedForHeaderKey = "X-Forwarded-For";
        public const string IsAuthTokenExpired = "IS-TOKEN-EXPIRED";
        public const string UserAgentHeaderKey = "User-Agent";
    }
}