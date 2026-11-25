namespace OsbbRequests.Services;

public enum AdminFailure
{
    InvalidInput,
    Unauthenticated,
    Forbidden,
    NotFound,
    Conflict,
    TooLarge,
    Unavailable
}

public sealed class AdminOperationException(AdminFailure failure, string message) : Exception(message)
{
    public AdminFailure Failure { get; } = failure;
}
