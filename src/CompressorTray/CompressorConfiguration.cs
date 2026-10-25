using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SystemCompressor.Dsp;

namespace SystemCompressor.Tray;

public sealed class CompressorConfiguration
{
    // Shared with the plug-in, which runs inside the Windows audio service as LOCAL SERVICE and
    // therefore cannot see the user's %LocalAppData%.
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SystemCompressor");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public CompressorSettings Settings { get; set; } = new();

    /// <summary>Creates the shared folder and lets LOCAL SERVICE (the audio service) read and write it.</summary>
    public static void PrepareDirectory()
    {
        try
        {
            var directory = Directory.CreateDirectory(DirectoryPath);
            var security = directory.GetAccessControl();
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static CompressorConfiguration Load()
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<CompressorConfiguration>(File.ReadAllText(FilePath)) ?? new();
            loaded.Settings = (loaded.Settings ?? new()).Validate();
            return loaded;
        }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    /// <summary>Saves without ever throwing: a missed write must not take the tray app down.</summary>
    public bool TrySave()
    {
        try { Save(); return LastSaveSucceeded = true; }
        catch (IOException) { return LastSaveSucceeded = false; }
        catch (UnauthorizedAccessException) { return LastSaveSucceeded = false; }
    }

    /// <summary>False while the settings file cannot be written (the plug-in then never sees changes).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LastSaveSucceeded { get; private set; } = true;

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporaryFile = FilePath + ".new";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        // The plug-in may be reading the file at this very moment; retry briefly instead of crashing.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporaryFile, FilePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(30);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(30);
            }
        }
    }
}
