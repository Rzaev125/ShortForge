using System.IO;
using ShortForge.Infrastructure;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunBatchSelection()
    {
        var library = new Library(Path.Combine(output, "data"));
        var project = library.Open(library.List().Single().Id);
        var selection = project.Selection ?? throw new InvalidDataException("Expected a saved selection.");
        var candidate = selection.Clips[0];
        foreach (var item in selection.Clips)
            library.MarkClip(project.Id, selection.Id, item.StartId, item == candidate ? "selected" : "offered");
        var edit = library.CreateEdit(project.Id, selection.Id, candidate.StartId);
        edit = library.SaveEdit(project.Id, edit.Id, edit.Revision, edit.Edit with {
            Title = "Исправленная подборка", EndUs = edit.Edit.StartUs + 2_000_000
        }, null);
        string lockFile = Path.Combine(output, "data", "export-worker.lock");
        await OpenBatchProject();
        await StartBatch(["Из подбора · выбрано 1", edit.Edit.Title], sidecars: false);
        await WaitForRows("queued", 1);
        await Contains("Возобновить очередь");
        Directory.Delete(lockFile);
        await Click("Возобновить очередь");
        await WaitForRows("completed", 1);
        var job = library.ListExportJobs().Single();
        if (job.ClipId != edit.Id || job.Revision != edit.Revision || job.Title != edit.Edit.Title ||
            !File.Exists(Path.Combine(library.ExportFolder(job.Id), job.FileName)))
            throw new InvalidDataException("Selected candidate did not use its saved edit exactly once.");
        Console.WriteLine("PASS visible persisted job resumed after restoring worker access; selected candidate plus duplicate saved edit produced one current MP4.");
    }
}
