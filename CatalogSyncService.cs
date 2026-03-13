using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PcatImageSyncApp;

internal sealed class CatalogSyncService
{
    private static readonly string[] RequiredImageFolders = new[] { "32", "64", "200" };

    private readonly List<string> _headerEntries = new List<string>();

    private readonly List<string> _logEntries = new List<string>();

    public static IReadOnlyList<CatalogInfo> DiscoverCatalogs(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return Array.Empty<CatalogInfo>();
        }

        var pcatFiles = Directory
            .EnumerateFiles(
                rootFolder,
                "*.pcat",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                })
            .OrderBy(path => Path.GetRelativePath(rootFolder, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var catalogs = new List<CatalogInfo>(pcatFiles.Length);
        foreach (var pcatFile in pcatFiles)
        {
            var supportFolder = ResolveSupportFolder(rootFolder, pcatFile);
            catalogs.Add(new CatalogInfo(rootFolder, pcatFile, supportFolder));
        }

        return catalogs;
    }

    public SyncRunResult Execute(CatalogInfo targetCatalog, IReadOnlyList<CatalogInfo> selectedSources)
    {
        ArgumentNullException.ThrowIfNull(targetCatalog);
        ArgumentNullException.ThrowIfNull(selectedSources);

        _headerEntries.Clear();
        _logEntries.Clear();

        var summary = new SyncSummary();
        AddHeader($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        AddHeader($"Target: {targetCatalog.PcatPath}");
        AddHeader("Sources:");
        foreach (var source in selectedSources)
        {
            AddHeader($"  - {source.PcatPath}");
        }

        if (!TryPrepareCatalog(targetCatalog, "target", out var preparedTarget, out var targetPreparationError))
        {
            Log(targetPreparationError);
            var fatalLog = WriteLogFile();
            return new SyncRunResult
            {
                Success = false,
                Summary = summary,
                FatalError = targetPreparationError,
                LogFilePath = fatalLog,
            };
        }

        var preparedSources = new List<PreparedCatalog>();
        foreach (var sourceCatalog in selectedSources)
        {
            if (TryPrepareCatalog(sourceCatalog, "source", out var preparedSource, out var sourcePreparationError))
            {
                preparedSources.Add(preparedSource!);
            }
            else
            {
                Log(sourcePreparationError);
            }
        }

        if (preparedSources.Count == 0)
        {
            Log("No usable source catalogs remain after validation.");
            var fatalLog = WriteLogFile();
            return new SyncRunResult
            {
                Success = false,
                Summary = summary,
                FatalError = "No usable source catalogs remain after validation.",
                LogFilePath = fatalLog,
            };
        }

        Console.WriteLine($"Loading target database: {preparedTarget!.Catalog.FileName}");
        List<EngineeringItemRecord> targetRecords;
        try
        {
            targetRecords = LoadEngineeringItems(preparedTarget.Catalog.PcatPath);
        }
        catch (Exception ex)
        {
            Log($"Failed to read target database '{preparedTarget.Catalog.PcatPath}': {ex.Message}");
            var fatalLog = WriteLogFile();
            return new SyncRunResult
            {
                Success = false,
                Summary = summary,
                FatalError = "Failed to read the target database.",
                LogFilePath = fatalLog,
            };
        }

        summary.TotalTargetRecords = targetRecords.Count;

        Console.WriteLine("Scanning target images...");
        var targetWorkItems = BuildTargetWorkItems(preparedTarget.ImageIndex, targetRecords, summary);
        summary.CandidateRecords = targetWorkItems.Count(item => item.MissingFolders.Count > 0);
        summary.AlreadyCompleteRecords = summary.TotalTargetRecords - summary.CandidateRecords - summary.InvalidTargetRecords;

        Console.WriteLine("Indexing source databases...");
        var sourceIndex = BuildSourceIndex(preparedSources);

        foreach (var workItem in targetWorkItems)
        {
            var record = workItem.TargetRecord;
            var isInitiallyComplete = workItem.Has32 && workItem.Has64 && workItem.Has200;
            var key = MatchKey.From(record);
            if (!sourceIndex.TryGetValue(key, out var hits) || hits.Count == 0)
            {
                if (!isInitiallyComplete)
                {
                    summary.UnresolvedRecords++;
                    LogMissingSourceMatch(workItem);
                }

                continue;
            }

            if (hits.Count > 1)
            {
                if (!isInitiallyComplete)
                {
                    summary.UnresolvedRecords++;
                    summary.AmbiguousMatchRecords++;
                    LogAmbiguousMatch(workItem, hits);
                }

                continue;
            }

            var uniqueHit = hits[0];
            Console.WriteLine($"Processing target row {record.RowNumber} / PartFamilyId {record.PartFamilyIdText}");
            var copyResult = CopyImages(workItem, uniqueHit, preparedTarget, preparedSources);
            summary.CopiedFiles += copyResult.CopiedFileCount;

            if (isInitiallyComplete)
            {
                continue;
            }

            if (copyResult.Resolved)
            {
                summary.ResolvedRecords++;
            }
            else if (copyResult.CopiedFileCount > 0)
            {
                summary.PartiallyResolvedRecords++;
                summary.UnresolvedRecords++;
                LogPartialResolution(workItem, uniqueHit, copyResult.MissingFoldersAfterCopy);
            }
            else
            {
                summary.UnresolvedRecords++;
                LogNoImagesFound(workItem, uniqueHit, copyResult.MissingFoldersAfterCopy);
            }
        }

        string? logFilePath = null;
        if (_logEntries.Count > 0)
        {
            logFilePath = WriteLogFile();
        }

        return new SyncRunResult
        {
            Success = true,
            Summary = summary,
            LogFilePath = logFilePath,
        };
    }

    private static string? ResolveSupportFolder(string rootFolder, string pcatPath)
    {
        var baseName = Path.GetFileNameWithoutExtension(pcatPath);
        var sameLevelFolder = Path.Combine(Path.GetDirectoryName(pcatPath)!, baseName);
        if (Directory.Exists(sameLevelFolder))
        {
            return sameLevelFolder;
        }

        var catalogSupportFolder = Path.Combine(rootFolder, "CatalogSupportFolder", baseName);
        if (Directory.Exists(catalogSupportFolder))
        {
            return catalogSupportFolder;
        }

        return null;
    }

    private bool TryPrepareCatalog(CatalogInfo catalog, string role, out PreparedCatalog? preparedCatalog, out string errorMessage)
    {
        preparedCatalog = null;

        if (!catalog.HasSupportFolder || string.IsNullOrWhiteSpace(catalog.SupportFolderPath))
        {
            errorMessage = $"The {role} catalog '{catalog.PcatPath}' was skipped because no support folder was found next to the .pcat file or under '{Path.Combine(catalog.RootFolder, "CatalogSupportFolder")}'.";
            return false;
        }

        try
        {
            EnsureRequiredImageFolders(catalog.SupportFolderPath);
            preparedCatalog = new PreparedCatalog(catalog, new SupportFolderImageIndex(catalog.SupportFolderPath));
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"The {role} catalog '{catalog.PcatPath}' could not be prepared: {ex.Message}";
            return false;
        }
    }

    private static void EnsureRequiredImageFolders(string supportFolder)
    {
        foreach (var folderName in RequiredImageFolders)
        {
            Directory.CreateDirectory(Path.Combine(supportFolder, folderName));
        }
    }

    private static List<EngineeringItemRecord> LoadEngineeringItems(string pcatPath)
    {
        var records = new List<EngineeringItemRecord>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = pcatPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    PartFamilyLongDesc,
    PartSizeLongDesc,
    PartFamilyId
FROM EngineeringItems;";

        using var reader = command.ExecuteReader();
        var rowNumber = 0;
        while (reader.Read())
        {
            rowNumber++;
            var partFamilyIdText = ReadGuidText(reader, 2);
            records.Add(new EngineeringItemRecord
            {
                RowNumber = rowNumber,
                PartFamilyLongDesc = ReadTextValue(reader, 0),
                PartSizeLongDesc = ReadTextValue(reader, 1),
                PartFamilyIdText = partFamilyIdText,
                PartFamilyIdCanonical = SupportFolderImageIndex.TryCanonicalizeGuid(partFamilyIdText),
            });
        }

        return records;
    }

    private static string ReadTextValue(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return string.Empty;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            string text => text.Trim(),
            byte[] bytes => Encoding.UTF8.GetString(bytes).Trim(),
            _ => value?.ToString()?.Trim() ?? string.Empty,
        };
    }

    private static string ReadGuidText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return string.Empty;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid guid => guid.ToString("D"),
            byte[] bytes when bytes.Length == 16 => new Guid(bytes).ToString("D"),
            byte[] bytes => NormalizeGuidText(Encoding.UTF8.GetString(bytes)),
            _ => NormalizeGuidText(value?.ToString()),
        };
    }

    private static string NormalizeGuidText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out var guid))
        {
            return guid.ToString("D");
        }

        return trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}'
            ? trimmed[1..^1].Trim()
            : trimmed;
    }

    private List<TargetWorkItem> BuildTargetWorkItems(SupportFolderImageIndex targetImageIndex, IReadOnlyList<EngineeringItemRecord> targetRecords, SyncSummary summary)
    {
        var workItems = new List<TargetWorkItem>();

        foreach (var record in targetRecords)
        {
            if (!record.HasValidPartFamilyId)
            {
                summary.InvalidTargetRecords++;
                summary.UnresolvedRecords++;
                Log($"Target row {record.RowNumber} has an invalid or empty PartFamilyId and cannot be processed. Family='{record.PartFamilyLongDesc}', Size='{record.PartSizeLongDesc}', PartFamilyId='{record.PartFamilyIdText}'.");
                continue;
            }

            var has32 = targetImageIndex.HasAnyFile("32", record);
            var has64 = targetImageIndex.HasAnyFile("64", record);
            var has200 = targetImageIndex.HasAnyFile("200", record);

            workItems.Add(new TargetWorkItem
            {
                TargetRecord = record,
                Has32 = has32,
                Has64 = has64,
                Has200 = has200,
            });
        }

        return workItems;
    }

    private Dictionary<MatchKey, List<SourceRecordHit>> BuildSourceIndex(IReadOnlyList<PreparedCatalog> preparedSources)
    {
        var result = new Dictionary<MatchKey, List<SourceRecordHit>>();

        foreach (var preparedSource in preparedSources)
        {
            Console.WriteLine($"  Reading source: {preparedSource.Catalog.FileName}");

            List<EngineeringItemRecord> sourceRecords;
            try
            {
                sourceRecords = LoadEngineeringItems(preparedSource.Catalog.PcatPath);
            }
            catch (Exception ex)
            {
                Log($"Source database '{preparedSource.Catalog.PcatPath}' could not be read and was skipped: {ex.Message}");
                continue;
            }

            foreach (var record in sourceRecords)
            {
                if (!record.HasValidPartFamilyId)
                {
                    Log($"Source database '{preparedSource.Catalog.PcatPath}' row {record.RowNumber} was ignored because PartFamilyId is invalid or empty. Family='{record.PartFamilyLongDesc}', Size='{record.PartSizeLongDesc}', PartFamilyId='{record.PartFamilyIdText}'.");
                    continue;
                }

                var key = MatchKey.From(record);
                if (!result.TryGetValue(key, out var hits))
                {
                    hits = new List<SourceRecordHit>();
                    result[key] = hits;
                }

                hits.Add(new SourceRecordHit
                {
                    Catalog = preparedSource.Catalog,
                    Record = record,
                });
            }
        }

        return result;
    }

    private CopyResult CopyImages(TargetWorkItem candidate, SourceRecordHit uniqueHit, PreparedCatalog preparedTarget, IReadOnlyList<PreparedCatalog> preparedSources)
    {
        var sourceCatalog = preparedSources.First(source => string.Equals(source.Catalog.PcatPath, uniqueHit.Catalog.PcatPath, StringComparison.OrdinalIgnoreCase));
        var targetRecord = candidate.TargetRecord;
        var copiedFileCount = 0;

        foreach (var folderName in RequiredImageFolders)
        {
            var sourceFiles = sourceCatalog.ImageIndex.GetFiles(folderName, uniqueHit.Record);
            if (sourceFiles.Count == 0)
            {
                continue;
            }

            foreach (var sourceFile in sourceFiles)
            {
                var originalFileName = Path.GetFileName(sourceFile);
                var destinationFileName = SupportFolderImageIndex.BuildRenamedFileName(
                    originalFileName,
                    uniqueHit.Record.PartFamilyIdCanonical!,
                    targetRecord.PartFamilyIdCanonical!);

                if (string.IsNullOrWhiteSpace(destinationFileName))
                {
                    Log($"Could not safely rename source image '{sourceFile}' for target row {targetRecord.RowNumber}. The source PartFamilyId '{uniqueHit.Record.PartFamilyIdText}' could not be mapped to target PartFamilyId '{targetRecord.PartFamilyIdText}'.");
                    continue;
                }

                if (!SupportFolderImageIndex.FileNameContainsGuid(destinationFileName, targetRecord.PartFamilyIdText, targetRecord.PartFamilyIdCanonical))
                {
                    Log($"Could not safely rename source image '{sourceFile}' for target row {targetRecord.RowNumber}. The destination file name '{destinationFileName}' does not contain target PartFamilyId '{targetRecord.PartFamilyIdText}'.");
                    continue;
                }

                var destinationPath = Path.Combine(preparedTarget.Catalog.SupportFolderPath!, folderName, destinationFileName);
                if (string.Equals(sourceFile, destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    preparedTarget.ImageIndex.AddFile(folderName, destinationPath);
                    continue;
                }

                try
                {
                    if (!File.Exists(destinationPath))
                    {
                        File.Copy(sourceFile, destinationPath, overwrite: false);
                        copiedFileCount++;
                    }

                    preparedTarget.ImageIndex.AddFile(folderName, destinationPath);
                }
                catch (Exception ex)
                {
                    Log($"Failed to copy '{sourceFile}' to '{destinationPath}': {ex.Message}");
                }
            }
        }

        var missingFoldersAfterCopy = RequiredImageFolders
            .Where(folderName => !preparedTarget.ImageIndex.HasAnyFile(folderName, targetRecord))
            .ToList();

        return new CopyResult(missingFoldersAfterCopy.Count == 0, copiedFileCount, missingFoldersAfterCopy);
    }

    private void LogMissingSourceMatch(TargetWorkItem candidate)
    {
        var record = candidate.TargetRecord;
        Log($"No source record found for target row {record.RowNumber}. Family='{record.PartFamilyLongDesc}', Size='{record.PartSizeLongDesc}', TargetPartFamilyId='{record.PartFamilyIdText}', MissingFolders={string.Join(",", candidate.MissingFolders)}.");
    }

    private void LogAmbiguousMatch(TargetWorkItem candidate, IReadOnlyList<SourceRecordHit> hits)
    {
        var record = candidate.TargetRecord;
        Log($"Ambiguous source match for target row {record.RowNumber}. Family='{record.PartFamilyLongDesc}', Size='{record.PartSizeLongDesc}', TargetPartFamilyId='{record.PartFamilyIdText}', MissingFolders={string.Join(",", candidate.MissingFolders)}. The following source rows matched and no images were copied:");
        foreach (var hit in hits)
        {
            Log($"  - SourcePcat='{hit.Catalog.PcatPath}', SourceRow={hit.Record.RowNumber}, SourcePartFamilyId='{hit.Record.PartFamilyIdText}'");
        }
    }

    private void LogNoImagesFound(TargetWorkItem candidate, SourceRecordHit hit, IReadOnlyList<string> missingFoldersAfterCopy)
    {
        var record = candidate.TargetRecord;
        Log($"A unique source record was found, but no required images could be copied for target row {record.RowNumber}. Family='{record.PartFamilyLongDesc}', Size='{record.PartSizeLongDesc}', TargetPartFamilyId='{record.PartFamilyIdText}', SourcePcat='{hit.Catalog.PcatPath}', SourcePartFamilyId='{hit.Record.PartFamilyIdText}', StillMissingFolders={string.Join(",", missingFoldersAfterCopy)}.");
    }

    private void LogPartialResolution(TargetWorkItem candidate, SourceRecordHit hit, IReadOnlyList<string> missingFoldersAfterCopy)
    {
        var record = candidate.TargetRecord;
        Log($"Images were copied only partially for target row {record.RowNumber}. Family='{record.PartFamilyLongDesc}', Size='{record.PartSizeLongDesc}', TargetPartFamilyId='{record.PartFamilyIdText}', SourcePcat='{hit.Catalog.PcatPath}', SourcePartFamilyId='{hit.Record.PartFamilyIdText}', StillMissingFolders={string.Join(",", missingFoldersAfterCopy)}.");
    }

    private void AddHeader(string message)
    {
        _headerEntries.Add(message);
    }

    private void Log(string message)
    {
        _logEntries.Add(message);
    }

    private string WriteLogFile()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, $"PcatImageSyncLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var content = new StringBuilder();
        foreach (var entry in _headerEntries)
        {
            content.AppendLine(entry);
        }

        if (_headerEntries.Count > 0 && _logEntries.Count > 0)
        {
            content.AppendLine();
        }

        foreach (var entry in _logEntries)
        {
            content.AppendLine(entry);
        }

        File.WriteAllText(logPath, content.ToString(), Encoding.UTF8);
        return logPath;
    }

    private sealed class PreparedCatalog
    {
        public PreparedCatalog(CatalogInfo catalog, SupportFolderImageIndex imageIndex)
        {
            Catalog = catalog;
            ImageIndex = imageIndex;
        }

        public CatalogInfo Catalog { get; }

        public SupportFolderImageIndex ImageIndex { get; }
    }

    private sealed class CopyResult
    {
        public CopyResult(bool resolved, int copiedFileCount, IReadOnlyList<string> missingFoldersAfterCopy)
        {
            Resolved = resolved;
            CopiedFileCount = copiedFileCount;
            MissingFoldersAfterCopy = missingFoldersAfterCopy;
        }

        public bool Resolved { get; }

        public int CopiedFileCount { get; }

        public IReadOnlyList<string> MissingFoldersAfterCopy { get; }
    }
}

internal sealed class SupportFolderImageIndex
{
    private static readonly Regex GuidRegex = new(@"\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, List<string>> _allFilesByFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, List<string>>> _filesByFolderAndGuid = new(StringComparer.OrdinalIgnoreCase);

    public SupportFolderImageIndex(string supportFolder)
    {
        SupportFolder = supportFolder;

        foreach (var folderName in new[] { "32", "64", "200" })
        {
            var fullFolderPath = Path.Combine(supportFolder, folderName);
            Directory.CreateDirectory(fullFolderPath);

            var files = Directory
                .EnumerateFiles(fullFolderPath, "*.png", SearchOption.TopDirectoryOnly)
                .ToList();

            _allFilesByFolder[folderName] = files;
            var byGuid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                IndexFile(byGuid, file);
            }

            _filesByFolderAndGuid[folderName] = byGuid;
        }
    }

    public string SupportFolder { get; }

    public bool HasAnyFile(string folderName, EngineeringItemRecord record)
    {
        return GetFiles(folderName, record).Count > 0;
    }

    public IReadOnlyList<string> GetFiles(string folderName, EngineeringItemRecord record)
    {
        var results = new List<string>();
        if (record.PartFamilyIdCanonical is not null
            && _filesByFolderAndGuid.TryGetValue(folderName, out var byGuid)
            && byGuid.TryGetValue(record.PartFamilyIdCanonical, out var indexedFiles))
        {
            results.AddRange(indexedFiles);
        }

        if (_allFilesByFolder.TryGetValue(folderName, out var allFiles))
        {
            foreach (var file in allFiles)
            {
                if (results.Any(existing => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (FileNameContainsGuid(file, record.PartFamilyIdText, record.PartFamilyIdCanonical))
                {
                    results.Add(file);
                }
            }
        }

        return results;
    }

    public void AddFile(string folderName, string filePath)
    {
        if (!_allFilesByFolder.TryGetValue(folderName, out var allFiles))
        {
            allFiles = new List<string>();
            _allFilesByFolder[folderName] = allFiles;
        }

        if (!allFiles.Any(existing => string.Equals(existing, filePath, StringComparison.OrdinalIgnoreCase)))
        {
            allFiles.Add(filePath);
        }

        if (!_filesByFolderAndGuid.TryGetValue(folderName, out var byGuid))
        {
            byGuid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _filesByFolderAndGuid[folderName] = byGuid;
        }

        IndexFile(byGuid, filePath);
    }

    public static string? TryCanonicalizeGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return Guid.TryParse(trimmed, out var guid)
            ? guid.ToString("D")
            : null;
    }

    public static bool FileNameContainsGuid(string filePathOrName, string guidText, string? canonicalGuid)
    {
        var fileName = Path.GetFileName(filePathOrName);

        if (!string.IsNullOrWhiteSpace(guidText) && fileName.Contains(guidText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(canonicalGuid) && fileName.Contains(canonicalGuid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (Match match in GuidRegex.Matches(fileName))
        {
            var matchCanonical = TryCanonicalizeGuid(match.Value);
            if (matchCanonical is not null && string.Equals(matchCanonical, canonicalGuid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string? BuildRenamedFileName(string originalFileName, string sourceGuidCanonical, string targetGuidCanonical)
    {
        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            return null;
        }

        var matches = GuidRegex.Matches(originalFileName);
        if (matches.Count > 0)
        {
            var builder = new StringBuilder(originalFileName);
            var replacedAny = false;

            for (var index = matches.Count - 1; index >= 0; index--)
            {
                var match = matches[index];
                var matchCanonical = TryCanonicalizeGuid(match.Value);
                if (matchCanonical is null || !string.Equals(matchCanonical, sourceGuidCanonical, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                builder.Remove(match.Index, match.Length);
                builder.Insert(match.Index, targetGuidCanonical);
                replacedAny = true;
            }

            if (replacedAny)
            {
                return builder.ToString();
            }
        }

        var replaced = Regex.Replace(originalFileName, Regex.Escape(sourceGuidCanonical), targetGuidCanonical, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return string.Equals(replaced, originalFileName, StringComparison.Ordinal) ? null : replaced;
    }

    private static void IndexFile(Dictionary<string, List<string>> byGuid, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        foreach (Match match in GuidRegex.Matches(fileName))
        {
            var canonicalGuid = TryCanonicalizeGuid(match.Value);
            if (canonicalGuid is null)
            {
                continue;
            }

            if (!byGuid.TryGetValue(canonicalGuid, out var files))
            {
                files = new List<string>();
                byGuid[canonicalGuid] = files;
            }

            if (!files.Any(existing => string.Equals(existing, filePath, StringComparison.OrdinalIgnoreCase)))
            {
                files.Add(filePath);
            }
        }
    }
}
