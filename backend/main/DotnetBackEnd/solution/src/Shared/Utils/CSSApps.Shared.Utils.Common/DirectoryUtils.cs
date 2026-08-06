namespace CSSApps.Shared.Utils.Common;

public static class DirectoryUtils
{
    public static void CreateDirectoryIfNotExists(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            Console.Error.WriteLine("Invalid directory path.");
            return;
        }

        // Check if path is a directory and not a file
        var isDirectory = Directory.Exists(path) || !Path.HasExtension(path);

        // Only proceed if the path is a directory or doesn't exist
        if (isDirectory || !File.Exists(path))
        {
            var directoryPath = isDirectory ? path : Path.GetDirectoryName(path);

            if (directoryPath is not null && !Directory.Exists(directoryPath))
                try
                {
                    Directory.CreateDirectory(directoryPath);
                    Console.WriteLine($"Created directory: {directoryPath}");
                }
                catch (Exception ex)
                {
                    // Handle exceptions (e.g., permission issues)
                    Console.Error.WriteLine($"Directory {directoryPath} creation: {ex.Message}");
                }
        }
    }
}