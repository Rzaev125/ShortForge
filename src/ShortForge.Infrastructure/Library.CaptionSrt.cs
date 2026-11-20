using System.Text;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed partial class Library
{
    public async Task WriteCaptionSrtAsync(Guid projectId, Guid clipId, int revision, string destination,
        CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(destination) || new Uri(destination).IsUnc ||
            !Path.GetExtension(destination).Equals(".srt", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(destination).Contains(':'))
            throw new UserError("srt_destination", "Выберите локальный файл с расширением .srt.");
        destination = Path.GetFullPath(destination);
        var clip = OpenEdit(projectId, clipId);
        if (clip.Revision != revision) throw ChangedEdit();
        if (clip.Cues.Length == 0) throw new UserError("no_captions", "В выбранном интервале нет фраз для SRT.");
        if (List().Any(p => string.Equals(Path.GetFullPath(p.OriginalPath), destination, StringComparison.OrdinalIgnoreCase)))
            throw new UserError("srt_source", "Этот файл используется как исходник проекта. Выберите другое имя.");
        var directory = Path.GetDirectoryName(destination)!;
        var temp = Path.Combine(directory, $".shortforge-{Guid.NewGuid():N}.tmp");
        var srt = CaptionFiles.ToSrt(clip);
        if (string.IsNullOrWhiteSpace(srt)) throw new UserError("no_captions", "Нет фраз, которые можно записать в SRT.");
        var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        try
        {
            await using (file)
                await file.WriteAsync(Encoding.UTF8.GetBytes(srt), token);
            PromoteCaptionFile(projectId,clipId,revision,temp,destination,true,token);
        }
        finally { File.Delete(temp); }
    }

    private void PromoteCaptionFile(Guid projectId, Guid clipId, int revision, string temp, string destination,
        bool overwrite, CancellationToken token)
    {
        PublishCurrentRevision(projectId, clipId, revision, token, () => File.Move(temp, destination, overwrite));
    }

    private void PublishCurrentRevision(Guid projectId, Guid clipId, int revision, CancellationToken token, Action publish,
        Guid? exportJob = null)
    {
        // Hold the SQLite writer lock across publication so a newer edit cannot slip between the check and rename.
        using var db = Connect();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT revision FROM edit_clip WHERE project_id=$project AND id=$clip";
        cmd.Parameters.AddWithValue("$project", projectId.ToString("D"));
        cmd.Parameters.AddWithValue("$clip", clipId.ToString("D"));
        if (cmd.ExecuteScalar() is not long current || current != revision) throw ChangedEdit();
        if (exportJob.HasValue)
        {
            cmd.Parameters.AddWithValue("$job", exportJob.Value.ToString("D"));
            cmd.CommandText = "SELECT cancel_requested FROM export_job WHERE id=$job";
            if (cmd.ExecuteScalar() is long cancelled && cancelled != 0) throw new OperationCanceledException(token);
            cmd.CommandText = "UPDATE export_job SET state='completed',percent=100,error=NULL WHERE id=$job AND state='running' AND cancel_requested=0";
            cmd.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested();
        publish();
        tx.Commit();
    }
}
