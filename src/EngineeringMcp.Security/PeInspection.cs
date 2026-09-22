using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace EngineeringMcp.Security;

/// <summary>
/// PE and managed-metadata inspection used to verify that a candidate executable is a real managed
/// WPF application before it can be authorized. Inspection never executes the binary.
/// </summary>
internal static class PeInspection
{
    internal static bool IsWpfExecutable(string executablePath)
    {
        if (!IsExecutableImage(executablePath)) return false;
        if (ReferencesWpfAssembly(executablePath)) return true;
        var companionAssembly = Path.ChangeExtension(executablePath, ".dll");
        return File.Exists(companionAssembly) &&
               IsAppHostBoundToCompanion(executablePath, companionAssembly) &&
               ReferencesWpfAssembly(companionAssembly);
    }

    private static bool IsExecutableImage(string executablePath)
    {
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var characteristics = peReader.PEHeaders.CoffHeader.Characteristics;
            return characteristics.HasFlag(Characteristics.ExecutableImage) &&
                   !characteristics.HasFlag(Characteristics.Dll);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsAppHostBoundToCompanion(string executablePath, string companionAssembly)
    {
        const long maximumAppHostBytes = 64L * 1024 * 1024;
        var executable = new FileInfo(executablePath);
        if (executable.Length <= 0 || executable.Length > maximumAppHostBytes) return false;
        var expectedName = Encoding.UTF8.GetBytes(Path.GetFileName(companionAssembly));
        var image = File.ReadAllBytes(executablePath);
        return image.AsSpan().IndexOf(expectedName) >= 0;
    }

    private static bool ReferencesWpfAssembly(string assemblyPath)
    {
        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata) return false;
            var metadata = peReader.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var name = metadata.GetString(reference.Name);
                if (name is "PresentationFramework" or "PresentationCore") return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
