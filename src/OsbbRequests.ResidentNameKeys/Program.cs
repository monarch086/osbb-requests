using System.Text.Json;
using OsbbRequests.Shared.Residents;

var names = JsonSerializer.Deserialize<ResidentNameInput[]>(await Console.In.ReadToEndAsync()) ?? [];
var keys = names.Select(name => ResidentNameOrder.Create(name.Id, name.FirstName, name.LastName)).ToArray();
var binary = Comparer<byte[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right));
foreach (var ascending in new[] { true, false })
{
    var comparison = Comparer<ResidentNameInput>.Create((left, right) => ResidentNameOrder.Compare(
        left.Id, left.FirstName, left.LastName, right.Id, right.FirstName, right.LastName, ascending));
    var expected = Enumerable.Range(0, names.Length).OrderBy(index => names[index], comparison);
    var actual = Enumerable.Range(0, names.Length).OrderBy(index => ascending ? keys[index].Ascending : keys[index].Descending, binary);
    if (!expected.SequenceEqual(actual))
    {
        Console.Error.WriteLine("Resident name keys do not match the directory sort order.");
        return 1;
    }
}
Console.WriteLine(JsonSerializer.Serialize(keys.Select(key => new { key.Ascending, key.Descending })));
return 0;

internal sealed record ResidentNameInput(string Id, string? FirstName, string? LastName);
