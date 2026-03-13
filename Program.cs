using System.Text;
using System.Windows.Forms;

namespace PcatImageSyncApp;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "PCAT Image Sync";

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var selectedFolder = SelectRootFolder();
            if (string.IsNullOrWhiteSpace(selectedFolder))
            {
                Console.WriteLine("No folder selected. Application cancelled.");
                return 1;
            }

            Console.WriteLine($"Selected folder: {selectedFolder}");
            Console.WriteLine("Scanning for .pcat files...");

            var catalogs = CatalogSyncService.DiscoverCatalogs(selectedFolder);
            if (catalogs.Count == 0)
            {
                MessageBox.Show("No .pcat files were found in the selected folder.", "PCAT Image Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Console.WriteLine("No .pcat files were found in the selected folder.");
                PauseBeforeExit();
                return 1;
            }

            if (catalogs.Count == 1)
            {
                MessageBox.Show("At least two .pcat files are required so one can be the source and another the target.", "PCAT Image Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Console.WriteLine("At least two .pcat files are required so one can be the source and another the target.");
                PauseBeforeExit();
                return 1;
            }

            using var selectionForm = new DatabaseSelectionForm(catalogs);
            var dialogResult = selectionForm.ShowDialog();
            if (dialogResult != DialogResult.OK || selectionForm.SelectedTarget is null || selectionForm.SelectedSources.Count == 0)
            {
                Console.WriteLine("Selection cancelled.");
                return 1;
            }

            var service = new CatalogSyncService();
            var runResult = service.Execute(selectionForm.SelectedTarget, selectionForm.SelectedSources);

            Console.WriteLine();
            Console.WriteLine("Run finished.");
            Console.WriteLine($"Total target rows:          {runResult.Summary.TotalTargetRecords}");
            Console.WriteLine($"Already complete rows:      {runResult.Summary.AlreadyCompleteRecords}");
            Console.WriteLine($"Invalid target rows:        {runResult.Summary.InvalidTargetRecords}");
            Console.WriteLine($"Rows needing images:        {runResult.Summary.CandidateRecords}");
            Console.WriteLine($"Resolved rows:              {runResult.Summary.ResolvedRecords}");
            Console.WriteLine($"Partially resolved rows:    {runResult.Summary.PartiallyResolvedRecords}");
            Console.WriteLine($"Ambiguous match rows:       {runResult.Summary.AmbiguousMatchRecords}");
            Console.WriteLine($"Unresolved rows:            {runResult.Summary.UnresolvedRecords}");
            Console.WriteLine($"Copied files:               {runResult.Summary.CopiedFiles}");

            if (!string.IsNullOrWhiteSpace(runResult.FatalError))
            {
                Console.WriteLine();
                Console.WriteLine($"Fatal error: {runResult.FatalError}");
            }

            if (!string.IsNullOrWhiteSpace(runResult.LogFilePath))
            {
                Console.WriteLine();
                Console.WriteLine($"Log written to: {runResult.LogFilePath}");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("No log file was needed.");
            }

            PauseBeforeExit();
            return string.IsNullOrWhiteSpace(runResult.FatalError) ? 0 : 1;
        }
        catch (Exception ex)
        {
            var fatalLogPath = WriteFatalErrorLog(ex);
            MessageBox.Show($"An unexpected error occurred. Details were written to:\n{fatalLogPath}", "PCAT Image Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Console.WriteLine("An unexpected error occurred.");
            Console.WriteLine(ex);
            Console.WriteLine($"Fatal log written to: {fatalLogPath}");
            PauseBeforeExit();
            return 1;
        }
    }

    private static string? SelectRootFolder()
    {
        using var folderBrowser = new FolderBrowserDialog
        {
            Description = "Select the root folder that contains the .pcat files.",
            ShowNewFolderButton = false,
        };

        return folderBrowser.ShowDialog() == DialogResult.OK
            ? folderBrowser.SelectedPath
            : null;
    }

    private static void PauseBeforeExit()
    {
        Console.WriteLine();
        Console.WriteLine("Press any key to close.");
        Console.ReadKey(intercept: true);
    }

    private static string WriteFatalErrorLog(Exception ex)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, $"PcatImageSync_FATAL_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(logPath, ex.ToString(), Encoding.UTF8);
        return logPath;
    }
}
