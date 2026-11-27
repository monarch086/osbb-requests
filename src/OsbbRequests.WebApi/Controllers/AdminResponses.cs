using Microsoft.AspNetCore.Mvc;

namespace OsbbRequests.WebApi.Controllers;

internal static class AdminResponses
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static OkObjectResult Saved() => new(new { ok = true });

    public static FileContentResult Workbook(byte[] contents) => new(contents, ExcelContentType);
}
