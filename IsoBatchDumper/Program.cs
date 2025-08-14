using System.Diagnostics;
using System.Threading;
using Ps3DiscDumper;
using WmiLight;

if (args.Length < 2)
{
    Console.WriteLine("Usage: <iso-source-directory> <output-directory>");
    return;
}

var sourceDir = args[0];
var outputRoot = args[1];
if (!Directory.Exists(sourceDir))
{
    Console.Error.WriteLine($"Source directory '{sourceDir}' does not exist.");
    return;
}
Directory.CreateDirectory(outputRoot);

var mounted = new List<string>();
try
{
    foreach (var isoPath in Directory.EnumerateFiles(sourceDir, "*.iso"))
    {
        var before = EnumerateDrives();
        using (var mountProc = Process.Start("powershell", $"Mount-DiskImage -ImagePath \"{isoPath}\""))
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
        var output = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(isoPath));
        try
        {
            using var dumper = new Dumper();
            dumper.DetectDisc(driveLetter + Path.DirectorySeparatorChar);
            await dumper.DumpAsync(output).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error dumping {isoPath}: {ex.Message}");
        }
        finally
        {
            using (var dismountProc = Process.Start("powershell", $"Dismount-DiskImage -ImagePath \"{isoPath}\""))
                dismountProc?.WaitForExit();
            mounted.Remove(isoPath);
        }
    }
}
finally
{
    foreach (var isoPath in mounted)
    {
        try
        {
            using var dismountProc = Process.Start("powershell", $"Dismount-DiskImage -ImagePath \"{isoPath}\"");
            dismountProc?.WaitForExit();
        }
        catch { }
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
