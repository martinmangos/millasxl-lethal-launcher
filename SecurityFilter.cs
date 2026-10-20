using System;
using System.IO;

public static class SecurityFilter
{
    public static readonly HashSet<string> BlockedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".scr",
        ".com",
        ".pif",
        ".bat",
        ".cmd",
        ".ps1",
        ".vbs",
        ".vbe",
        ".js",
        ".jse",
        ".wsf",
        ".wsh",
        ".hta",
        ".msi",
        ".msix",
        ".sys",
        ".drv",
        ".cpl",
        ".ocx",
        ".lnk",
        ".reg",
        ".inf"
    };

    public static bool IsDangerous(string fileName)
    {
        string? extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        return BlockedExtensions.Contains(extension);
    }

    public static List<string> FilterZipEntries(string extractedFolderPath)
    {
        var deletedFiles = new List<string>();

        foreach (string file in Directory.GetFiles(extractedFolderPath, "*", SearchOption.AllDirectories))
        {
            if (IsDangerous(file))
            {
                File.Delete(file);
                deletedFiles.Add(file);
            }
        }

        return deletedFiles;
    }
}
