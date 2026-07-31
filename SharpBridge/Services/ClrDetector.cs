using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace SharpBridge.Services;

/// <summary>
/// Detects whether a target is a .NET CLR process or assembly.
/// Used to pre-validate launch/attach targets before handing off to SharpDbg.
/// </summary>
public static class ClrDetector
{
    // ===================================================================
    // Assembly detection (file on disk)
    // ===================================================================

    /// <summary>
    /// Check whether <paramref name="filePath"/> is a .NET assembly by reading
    /// its PE header. Handles the Native AppHost case where the .exe is a
    /// native wrapper and the real .NET assembly is the adjacent .dll.
    /// </summary>
    public static bool IsDotNetAssembly(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        return CheckCorHeader(filePath)
               || (Path.GetExtension(filePath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                   && CheckCorHeader(Path.ChangeExtension(filePath, ".dll")));
    }

    private static bool CheckCorHeader(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            return peReader.HasMetadata && peReader.PEHeaders.CorHeader != null;
        }
        catch
        {
            return false;
        }
    }

    // ===================================================================
    // Process detection (running process)
    // ===================================================================

    /// <summary>
    /// Check whether a running process has the CLR loaded.
    /// The CLR loads within the first ~1 second of a .NET process's life, so a
    /// process up for 2+ seconds has either loaded its CLR or never will —
    /// no polling needed. A younger process is given time to finish loading
    /// before the module check.
    /// </summary>
    public static bool IsDotNetProcess(int processId)
    {
        DateTime startTime;
        try
        {
            using var proc = Process.GetProcessById(processId);
            startTime = proc.StartTime;
        }
        catch
        {
            return false; // process not found or not accessible
        }

        var elapsed = DateTime.Now - startTime;
        if (elapsed < TimeSpan.FromSeconds(2))
            Thread.Sleep(TimeSpan.FromSeconds(2) - elapsed);

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return HasClrModuleOnWindows(processId);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return HasClrModuleOnLinux(processId);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return HasClrModuleOnMacOS(processId);

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasClrModuleOnWindows(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.Modules
                .Cast<ProcessModule>()
                .Any(m =>
                {
                    var name = m.ModuleName?.ToLowerInvariant();
                    return name == "coreclr.dll" || name == "clr.dll";
                });
        }
        catch
        {
            return false;
        }
    }

    private static bool HasClrModuleOnLinux(int processId)
    {
        try
        {
            var mapsPath = $"/proc/{processId}/maps";
            if (!File.Exists(mapsPath))
                return false;

            foreach (var line in File.ReadLines(mapsPath))
            {
                if (line.Contains("libcoreclr.so"))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasClrModuleOnMacOS(int processId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "vmmap",
                Arguments = $"-w {processId}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output.Contains("libcoreclr.dylib");
        }
        catch
        {
            return false;
        }
    }
}
