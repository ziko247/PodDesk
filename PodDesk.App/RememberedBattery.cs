using System;
using System.IO;
using System.Text.Json;

namespace PodDesk;

internal sealed record RememberedBattery(ulong Pair, ushort Product, ulong Broadcast, byte ColorCode)
{
    private static readonly string Location = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PodDesk", "battery-pair.json");

    public static RememberedBattery? Load()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<RememberedBattery>(File.ReadAllText(Location));
            return saved is { Pair: > 0 and <= 0xFFFFFFFFFFFF, Broadcast: > 0 and <= 0xFFFFFFFFFFFF } ? saved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public bool Save()
    {
        var temporary = Location + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, Location, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
