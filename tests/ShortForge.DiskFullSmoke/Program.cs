using System.Security.Cryptography;
using ShortForge.Infrastructure;

string folder = args[0], bin = args[1];
string ffmpeg = Path.Combine(bin, "ffmpeg.exe"), ffprobe = Path.Combine(bin, "ffprobe.exe");
string source = Path.Combine(folder, "source.mp4"), volume = Path.Combine(folder, "mount");
string writable = Directory.CreateDirectory(Path.Combine(folder, "writable")).FullName;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
using var original = File.OpenRead(source);
byte[] digest = SHA256.HashData(original);
var library = new Library(Path.Combine(folder, "data"));
var project = await library.ImportAsync(source, ffprobe, null, deadline.Token);
var clip = library.CreateEdit(project.Id);
var first = library.EnqueueExports(project.Id, [clip.Id], volume, new()).Single();
var second = library.EnqueueExports(project.Id, [clip.Id], writable, new()).Single();
await library.RunExportQueueAsync(() => (ffmpeg, ffprobe), deadline.Token);
var saved = new Library(Path.Combine(folder, "data")).ListExportJobs().ToDictionary(j => j.Id);
Console.WriteLine($"Quota job {first.Id} {saved[first.Id].State}: {saved[first.Id].Error}");
Console.WriteLine($"Following job {second.Id} {saved[second.Id].State}: {saved[second.Id].Error}");
if (saved[first.Id].State != "failed" || saved[first.Id].Error != "На диске назначения недостаточно свободного места."
    || saved[second.Id].State != "completed" || saved[second.Id].Percent != 100)
    throw new InvalidDataException("Persisted queue state mismatch.");
var files = Directory.GetFileSystemEntries(volume).Select(Path.GetFileName).ToArray();
if (files.Length != 1 || files[0] != "identity.txt")
    throw new InvalidDataException("Failed job left output in private volume: " + string.Join(", ", files));
var result = Path.Combine(library.ExportFolder(second.Id), second.FileName);
if (new FileInfo(result).Length <= 65536) throw new InvalidDataException("Control encode did not exceed quota.");
original.Position = 0;
if (!digest.SequenceEqual(SHA256.HashData(original))) throw new InvalidDataException("Source changed.");
Console.WriteLine($"PASS following MP4 {new FileInfo(result).Length} bytes; source SHA256 {Convert.ToHexString(digest)}");
