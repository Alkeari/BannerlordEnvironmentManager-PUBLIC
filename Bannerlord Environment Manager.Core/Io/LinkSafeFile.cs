namespace BannerlordEnvironmentManager.Core.Io;

// Writing over a file in a game folder without writing into it. Restore This Save's Setup hard-links a
// copied game's files to the install they came from, so the copy and that install share one set of bytes
// per file: anything that opens an existing file and writes into it (File.Copy with overwrite, a
// WriteAllBytes over it) changes both. Writing a new file beside it and moving it over the name replaces
// only that name, which leaves the other install exactly as it was.
public static class LinkSafeFile
{
    public static void Copy(string source, string destination)
    {
        var temporary = destination + ".bem-new";
        File.Copy(source, temporary, overwrite: true);
        File.Move(temporary, destination, overwrite: true);
    }

    public static void WriteAllBytes(string destination, byte[] bytes)
    {
        var temporary = destination + ".bem-new";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, destination, overwrite: true);
    }
}
