namespace OsbbRequests.Shared.Residents;

public sealed record ResidentRecordKey(string Key, string Group, string Order)
{
    public const string IndexGroupAttribute = "recordIndexGroup";
    public const string CompactIndex = "ResidentRecordKeysByGroup";
    public const string FullIndex = "ResidentRecordsByGroup";

    public static ResidentRecordKey Create(string complexId, string sourcePk, string sourceSk) =>
        new("COMPLEX#" + complexId + "#RECORD#" + sourcePk + "#" + sourceSk,
            "COMPLEX#" + complexId + "#" + sourcePk, sourceSk);

    public static bool IsDirectoryOrHistory(string complexId, string sourcePk, string sourceSk) =>
        (sourcePk == "BUILDING#" + complexId && sourceSk.StartsWith("RESIDENT#", StringComparison.Ordinal)) ||
        (sourcePk.StartsWith("REGISTRATION#", StringComparison.Ordinal) && sourceSk.StartsWith("EVENT#TYPE#", StringComparison.Ordinal));
}
