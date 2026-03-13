namespace PcatImageSyncApp;

internal sealed class CatalogInfo
{
    public CatalogInfo(string rootFolder, string pcatPath, string? supportFolderPath)
    {
        RootFolder = rootFolder;
        PcatPath = pcatPath;
        SupportFolderPath = supportFolderPath;
    }

    public string RootFolder { get; }

    public string PcatPath { get; }

    public string? SupportFolderPath { get; }

    public string FileName => Path.GetFileName(PcatPath);

    public string RelativePath => Path.GetRelativePath(RootFolder, PcatPath);

    public string Name => Path.GetFileNameWithoutExtension(PcatPath);

    public bool HasSupportFolder => !string.IsNullOrWhiteSpace(SupportFolderPath);

    public string DisplayName => HasSupportFolder
        ? RelativePath
        : $"{RelativePath} [support folder not found]";

    public override string ToString() => DisplayName;
}

internal sealed class EngineeringItemRecord
{
    public int RowNumber { get; init; }

    public string PartFamilyLongDesc { get; init; } = string.Empty;

    public string PartSizeLongDesc { get; init; } = string.Empty;

    public string PartFamilyIdText { get; init; } = string.Empty;

    public string? PartFamilyIdCanonical { get; init; }

    public bool HasValidPartFamilyId => PartFamilyIdCanonical is not null;
}

internal sealed class SourceRecordHit
{
    public required CatalogInfo Catalog { get; init; }

    public required EngineeringItemRecord Record { get; init; }
}

internal sealed class TargetWorkItem
{
    public required EngineeringItemRecord TargetRecord { get; init; }

    public bool Has32 { get; init; }

    public bool Has64 { get; init; }

    public bool Has200 { get; init; }

    public IReadOnlyList<string> MissingFolders
    {
        get
        {
            var result = new List<string>();
            if (!Has32)
            {
                result.Add("32");
            }

            if (!Has64)
            {
                result.Add("64");
            }

            if (!Has200)
            {
                result.Add("200");
            }

            return result;
        }
    }
}

internal readonly record struct MatchKey(string Family, string Size)
{
    public static MatchKey From(string? family, string? size)
    {
        return new MatchKey(Normalize(family), Normalize(size));
    }

    public static MatchKey From(EngineeringItemRecord record)
    {
        return From(record.PartFamilyLongDesc, record.PartSizeLongDesc);
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }
}

internal sealed class SyncSummary
{
    public int TotalTargetRecords { get; set; }

    public int AlreadyCompleteRecords { get; set; }

    public int InvalidTargetRecords { get; set; }

    public int CandidateRecords { get; set; }

    public int ResolvedRecords { get; set; }

    public int PartiallyResolvedRecords { get; set; }

    public int UnresolvedRecords { get; set; }

    public int AmbiguousMatchRecords { get; set; }

    public int CopiedFiles { get; set; }
}

internal sealed class SyncRunResult
{
    public required bool Success { get; init; }

    public required SyncSummary Summary { get; init; }

    public string? LogFilePath { get; init; }

    public string? FatalError { get; init; }
}
