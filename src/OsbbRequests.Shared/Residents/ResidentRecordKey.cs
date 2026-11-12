namespace OsbbRequests.Shared.Residents;

public sealed record ResidentRecordKey(string Key, string Group, string Order)
{
    public static ResidentRecordKey Create(string complexId, string sourcePk, string sourceSk) =>
        new("COMPLEX#" + complexId + "#RECORD#" + sourcePk + "#" + sourceSk,
            "COMPLEX#" + complexId + "#" + sourcePk, sourceSk);
}
