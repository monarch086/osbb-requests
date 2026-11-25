using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[ApiController]
[Authorize(Policy = CognitoAuthentication.AdminScope)]
[RequireAdminAccess]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public abstract class AdminControllerBase : ControllerBase
{
    private const int MaxBodyLength = 10000;
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    protected AdminUser Actor => HttpContext.Features.Get<AdminUser>() ??
        throw new InvalidOperationException("The administrative authorization requirement did not load an account.");

    protected string? Query(string name) =>
        Request.Query.TryGetValue(name, out var value) ? value.ToString() : null;

    protected ResidentDirectoryFilters DirectoryFilters() => new(Query("building"), Query("residentType"), Query("sort"), Query("direction"));

    protected OkObjectResult Saved() => Ok(new { ok = true });

    protected FileContentResult Workbook(byte[] contents) => File(contents, ExcelContentType);

    protected async Task<T> ReadBodyAsync<T>(CancellationToken ct)
    {
        var body = new char[MaxBodyLength + 1];
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        try
        {
            var length = await reader.ReadBlockAsync(body, ct);
            if (length > MaxBodyLength) throw new JsonException();
            var options = HttpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
            return JsonSerializer.Deserialize<T>(body.AsSpan(0, length), options) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректні дані запиту.");
        }
    }
}
