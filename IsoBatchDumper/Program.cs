using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Threading;
using Ps3DiscDumper;
using WmiLight;

string? sourceDir = null;
string? outputRoot = null;
var dryRun = false;
var recurse = false;
var delete = false;

foreach (var arg in args)
{
    switch (arg)
    {
        case "--dry-run":
        case "-n":
            dryRun = true;
            break;
        case "--recurse":
        case "-r":
            recurse = true;
            break;
        case "-d":
        case "--delete":
            delete = true;
            break;
        default:
            if (sourceDir is null)
                sourceDir = arg;
            else if (outputRoot is null)
                outputRoot = arg;
            else
            {
                Console.WriteLine("Usage: <iso-source-directory> <output-directory> [--dry-run] [--recurse]");
                return;
            }
            break;
    }
}

if (sourceDir is null || outputRoot is null)
{
    Console.WriteLine("Usage: <iso-source-directory> <output-directory> [--dry-run] [--recurse]");
    return;
}

if (dryRun)
    Console.WriteLine("Dry run: no discs will be dumped");

if (!Directory.Exists(sourceDir))
{
    Console.Error.WriteLine($"Source directory '{sourceDir}' does not exist.");
    return;
}
Directory.CreateDirectory(outputRoot);

var mounted = new List<string>();
try
{
    foreach (var isoPath in Directory.EnumerateFiles(sourceDir, "*.iso", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order())
    {
        var cleanPath = isoPath.Replace("\'", "''");
        bool successfulDump = false;
        var output = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(isoPath));
        if (dryRun)
        {
            Console.WriteLine($"[Dry Run] Would dump {isoPath} to {output}");
            continue;
        }

        var before = EnumerateDrives();

        using (var mountProc = Process.Start("powershell", $"Mount-DiskImage -ImagePath '{cleanPath}'"))
            mountProc?.WaitForExit();
        var newDrive = WaitForDrive(before);
        if (newDrive is null)
        {
            Console.Error.WriteLine($"Failed to find mounted drive for {isoPath}");
            continue;
        }
        mounted.Add(isoPath);
        var (deviceId, driveLetter) = newDrive.Value;
        Console.WriteLine($"Mounted {isoPath} as {deviceId} ({driveLetter})");
        try
        {
            using var dumper = new Dumper();
            dumper.DetectDisc(driveLetter + Path.DirectorySeparatorChar);
            await dumper.FindDiscKeyAsync(SettingsProvider.Settings.IrdDir)
                .ConfigureAwait(false);
            await dumper.DumpAsync(output).ConfigureAwait(false);
            successfulDump = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error dumping {isoPath}: {ex.Message}");
        }
        finally
        {
            using (var dismountProc = Process.Start("powershell", $"Dismount-DiskImage -ImagePath '{cleanPath}'"))
                dismountProc?.WaitForExit();
            mounted.Remove(isoPath);
        }
        if (delete && successfulDump)
        {
            try
            {
                File.Delete(isoPath);
                Console.WriteLine($"Delete iso at {isoPath}");
            }catch(Exception ex)
            {
                Console.Error.WriteLine($"Unable to delete {isoPath}, error follows:\r\n" +ex.ToString());
            }
        }
    }
}
finally
{
    if (!dryRun)
    {
        foreach (var isoPath in mounted)
        {
            try
            {
                var cleanPath = isoPath.Replace("\'", "\\'");
                using var dismountProc = Process.Start("powershell", $"Dismount-DiskImage -ImagePath '{cleanPath}'");
                dismountProc?.WaitForExit();
            }
            catch { }
        }
    }
}

static Dictionary<string, string?> EnumerateDrives()
{
    var result = new Dictionary<string, string?>();
    using var wmi = new WmiConnection();
    var drives = wmi.CreateQuery("SELECT * FROM Win32_CDROMDrive");
    foreach (var drive in drives)
    {
        var lun = drive["SCSILogicalUnit"]?.ToString();
        if (lun is null) continue;
        var device = $"\\\\.\\CDROM{lun}";
        result[device] = drive["Drive"] as string;
    }
    return result;
}

static (string DeviceId, string DriveLetter)? WaitForDrive(Dictionary<string, string?> before)
{
    for (var i = 0; i < 10; i++)
    {
        Thread.Sleep(500);
        var after = EnumerateDrives();
        var newDrive = after.FirstOrDefault(kv => !before.ContainsKey(kv.Key) && !string.IsNullOrEmpty(kv.Value));
        if (!string.IsNullOrEmpty(newDrive.Key) && !string.IsNullOrEmpty(newDrive.Value))
            return (newDrive.Key, newDrive.Value!);
    }
    return null;
}
