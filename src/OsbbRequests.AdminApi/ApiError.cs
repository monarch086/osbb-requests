namespace OsbbRequests.AdminApi;

public sealed class ApiError(int status, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public int Status { get; } = status;
}
