namespace ApiAggregation.Domain.Common.Results;

public enum ResultErrorCode
{
    Unspecified = 0,
    NotFound = 10,
    ValidationError = 20,
    Unauthorized = 30,
    Forbidden = 40,
    Conflict = 50,
    RateLimited = 60,
    InternalError = 70,
    ServiceUnavailable = 80
}
