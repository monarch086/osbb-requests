namespace OsbbRequests.Bot.Commands;

public enum ResidentApprovalMode { Automatic, Manual }

public static class ResidentApprovalConfiguration
{
    public static ResidentApprovalMode Parse(string? value) => value switch
    {
        null or "" or "Automatic" => ResidentApprovalMode.Automatic,
        "Manual" => ResidentApprovalMode.Manual,
        _ => throw new InvalidOperationException("RESIDENT_APPROVAL_MODE must be Automatic or Manual.")
    };
}
