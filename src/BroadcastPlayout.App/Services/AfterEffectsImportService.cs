using System.Diagnostics;
using System.Drawing;

namespace BroadcastPlayout.Services;

/// <summary>
/// Adobe After Effects bridge used by CG Editor. Kashtrix does not attempt to reverse-engineer
/// the proprietary binary AEP format. Instead, when After Effects is installed, aerender renders
/// the requested composition to an alpha-capable image sequence which is then played by the native
/// Kashtrix image-sequence timeline. AEPX and AET use the same supported After Effects render path.
/// </summary>
public static class AfterEffectsImportService
{
    public sealed record Result(
        string ProjectPath,
        string Composition,
        string SequenceFolder,
        double FrameRate,
        int Width,
        int Height,
        int FrameCount,
        string OutputModuleTemplate,
        string AerenderPath);

    public static string? FindAerender()
    {
        var configured = Environment.GetEnvironmentVariable("KASHTRIX_AFTERFX_AERENDER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var adobe = Path.Combine(root, "Adobe");
            if (!Directory.Exists(adobe)) continue;
            try
            {
                foreach (var folder in Directory.EnumerateDirectories(adobe, "Adobe After Effects *")
                             .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var candidate in new[]
                             {
                                 Path.Combine(folder, "Support Files", "aerender.exe"),
                                 Path.Combine(folder, "aerender.exe")
                             })
                        if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
        }
        return null;
    }

    public static async Task<Result> RenderCompositionAsync(
        string projectPath,
        string composition,
        double frameRate,
        string outputModuleTemplate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            throw new FileNotFoundException("After Effects project was not found.", projectPath);
        var extension = Path.GetExtension(projectPath);
        if (!new[] { ".aep", ".aepx", ".aet" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an After Effects .aep, .aepx, or .aet project file.");

        var aerender = FindAerender() ?? throw new FileNotFoundException(
            "Adobe After Effects aerender.exe was not found. Install After Effects or set KASHTRIX_AFTERFX_AERENDER to aerender.exe.");

        if (string.IsNullOrWhiteSpace(composition))
            throw new InvalidDataException("Enter the After Effects composition name to import. Kashtrix renders one named composition so the output sequence is deterministic.");

        var comp = composition.Trim();
        var fps = double.IsFinite(frameRate) ? Math.Clamp(frameRate, 1, 120) : 25;
        var template = string.IsNullOrWhiteSpace(outputModuleTemplate) ? "PNG Sequence with Alpha" : outputModuleTemplate.Trim();
        var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(projectPath));
        var safeComp = SanitizeFileName(comp);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KashtrixPlayout", "CgAssets", "AfterEffects");
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, $"{safeName}-{safeComp}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, "frame_[#####].png");

        var start = new ProcessStartInfo
        {
            FileName = aerender,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory
        };
        start.ArgumentList.Add("-project"); start.ArgumentList.Add(projectPath);
        start.ArgumentList.Add("-comp"); start.ArgumentList.Add(comp);
        start.ArgumentList.Add("-OMtemplate"); start.ArgumentList.Add(template);
        start.ArgumentList.Add("-output"); start.ArgumentList.Add(output);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Unable to start Adobe aerender.exe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        var files = Directory.EnumerateFiles(folder)
            .Where(x => new[] { ".png", ".tif", ".tiff", ".jpg", ".jpeg", ".bmp" }
                .Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (process.ExitCode != 0 || files.Length == 0)
        {
            try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); } catch { }
            var detail = string.Join(Environment.NewLine,
                new[] { stderr, stdout }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            if (detail.Length > 1800) detail = detail[^1800..];
            throw new InvalidOperationException(
                $"After Effects render did not create an image sequence. Exit code {process.ExitCode}. " +
                $"Verify composition name '{comp}' and Output Module template '{template}'." +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : Environment.NewLine + detail));
        }

        var width = 1920; var height = 1080;
        try
        {
            using var first = Image.FromFile(files[0]);
            width = first.Width; height = first.Height;
        }
        catch { }

        File.WriteAllText(Path.Combine(folder, "kashtrix-after-effects-source.txt"),
            $"Project={projectPath}{Environment.NewLine}Composition={comp}{Environment.NewLine}FrameRate={fps:0.###}{Environment.NewLine}OutputModule={template}{Environment.NewLine}Aerender={aerender}{Environment.NewLine}",
            new System.Text.UTF8Encoding(false));

        return new Result(projectPath, comp, folder, fps, width, height, files.Length, template, aerender);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((value ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "AfterEffects" : clean;
    }
}
